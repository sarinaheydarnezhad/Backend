using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cent.Module;

public sealed class CentReaderBudget
{
    public string Key { get; set; } = "";
    public string StateJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

// Cent database only. Shared account/IP partitions arbitrate replicas without new infrastructure.
public sealed class CentReaderProtection(CentDbContext db, IConfiguration config, ILogger<CentReaderProtection> logger, TimeProvider clock)
{
    public sealed record Retrieval(DateTimeOffset At, Guid Content);
    public sealed record ActiveLease(Guid Id, DateTimeOffset Until);
    public sealed class State
    {
        public List<Retrieval> Requests { get; set; } = [];
        public List<ActiveLease> Leases { get; set; } = [];
        public DateTimeOffset BlockedUntil { get; set; }
    }
    public int Setting(string key, int fallback) => int.TryParse(config["Cent:Reader:Protection:" + key], out var n) ? Math.Clamp(n, 1, 100000) : fallback;

    public async Task<IAsyncDisposable> Enter(Guid account, string ip, Guid content, bool asset, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var group = asset ? "asset" : "body";
        var keys = new[] { $"{group}:account:{CentModule.Hash(account.ToString())}", $"{group}:ip:{CentModule.Hash(ip)}" };
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await Lock(keys, ct);
        var rows = new List<CentReaderBudget>(); var states = new List<State>();
        var window = Setting("LongWindowSeconds", 1800);
        foreach (var key in keys) {
            var row = await db.Set<CentReaderBudget>().SingleOrDefaultAsync(b => b.Key == key, ct);
            if (row is null) { row = new() { Key = key }; db.Add(row); }
            else await db.Entry(row).ReloadAsync(ct);
            var state = JsonSerializer.Deserialize<State>(row.StateJson)!;
            state.Requests.RemoveAll(r => r.At <= now.AddSeconds(-Math.Max(60, window)));
            state.Leases.RemoveAll(l => l.Until <= now);
            rows.Add(row); states.Add(state);
        }
        CentException? rejection = null;
        for (var i = 0; i < states.Count; i++) {
            var state = states[i];
            var limit = Setting((asset ? "Asset" : "") + (i == 0 ? "AccountPerMinute" : "IpPerMinute"), asset ? i == 0 ? 360 : 2400 : i == 0 ? 60 : 600);
            var concurrency = Setting(i == 0 ? "AccountConcurrent" : "IpConcurrent", i == 0 ? 8 : 64);
            var minute = state.Requests.Where(r => r.At > now.AddMinutes(-1)).ToArray();
            var recent = state.Requests.Where(r => r.At > now.AddSeconds(-10)).ToArray();
            var longRequests = state.Requests.Where(r => r.At > now.AddSeconds(-window)).ToArray();
            var reason = state.BlockedUntil > now ? "cooldown" : state.Leases.Count >= concurrency ? "concurrency" :
                minute.Length >= limit ? "rate" : !asset && i == 0 && recent.Length >= Setting("BurstPer10Seconds", 12) ? "speed" :
                !asset && i == 0 && recent.Select(r => r.Content).Append(content).Distinct().Count() > Setting("UniquePer10Seconds", 12) ? "enumeration" :
                longRequests.Length >= Setting(asset ? i == 0 ? "AssetAccountLongRequests" : "AssetIpLongRequests" : i == 0 ? "AccountLongRequests" : "IpLongRequests", asset ? i == 0 ? 6000 : 40000 : i == 0 ? 600 : 12000) ? "long-rate" :
                !asset && i == 0 && longRequests.Select(r => r.Content).Append(content).Distinct().Count() > Setting("UniquePerLongWindow", 120) ? "slow-enumeration" : null;
            if (reason is null) continue;
            if (reason != "cooldown") {
                state.BlockedUntil = now.AddSeconds(Setting("CooldownSeconds", 10));
                if (reason == "rate") state.BlockedUntil = Later(state.BlockedUntil, minute[0].At.AddMinutes(1));
                if (reason is "long-rate" or "slow-enumeration") state.BlockedUntil = Later(state.BlockedUntil, longRequests[0].At.AddSeconds(window));
            }
            var retry = Math.Max(1, (int)Math.Ceiling((state.BlockedUntil - now).TotalSeconds));
            logger.LogWarning("Cent reader throttle {Reason}; accountHash {AccountHash}; ipHash {IpHash}; content {Content}; retry {Retry}", reason, CentModule.Hash(account.ToString())[..16], CentModule.Hash(ip)[..16], content, retry);
            rejection = new(429, "reader_throttled", "Please retry after the indicated interval.", retry); break;
        }
        var leaseId = Guid.NewGuid();
        foreach (var state in states) if (rejection is null) {
            state.Requests.Add(new(now, content)); state.Leases.Add(new(leaseId, now.AddSeconds(Setting("LeaseSeconds", 120))));
        }
        for (var i = 0; i < rows.Count; i++) { rows[i].StateJson = JsonSerializer.Serialize(states[i]); rows[i].UpdatedAt = now; }
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        if (rejection is not null) throw rejection;
        return new Lease(async () => {
            await using var release = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, CancellationToken.None);
            await Lock(keys, CancellationToken.None);
            foreach (var key in keys) {
                var row = await db.Set<CentReaderBudget>().SingleAsync(b => b.Key == key);
                await db.Entry(row).ReloadAsync();
                var state = JsonSerializer.Deserialize<State>(row.StateJson)!; state.Leases.RemoveAll(l => l.Id == leaseId);
                row.StateJson = JsonSerializer.Serialize(state);
            }
            await db.SaveChangesAsync(); await release.CommitAsync();
        });
    }
    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    internal async Task Lock(string[] keys, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) return; // SQLite serializable write transactions arbitrate isolated tests.
        foreach (var key in keys.Order(StringComparer.Ordinal)) {
            var resource = "Cent.Reader." + key;
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=3000; SELECT @r;";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@resource"; parameter.Value = resource; command.Parameters.Add(parameter);
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            if (result < 0) throw new CentException(429, "reader_busy", "Please retry shortly.", 3);
        }
    }
    private sealed class Lease(Func<Task> release) : IAsyncDisposable
    {
        private Func<Task>? action = release;
        public async ValueTask DisposeAsync() { var handler = Interlocked.Exchange(ref action, null); if (handler is not null) await handler(); }
    }
}
