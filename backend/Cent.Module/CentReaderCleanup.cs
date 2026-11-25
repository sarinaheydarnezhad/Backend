using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cent.Module;

public sealed class CentReaderCleanup(CentDbContext db, IConfiguration config, TimeProvider clock)
{
    public async Task<int> Run(CancellationToken ct)
    {
        int Setting(string name, int fallback, int maximum) => int.TryParse(config["Cent:Reader:Protection:" + name], out var n) ? Math.Clamp(n, 1, maximum) : fallback;
        var now = clock.GetUtcNow();
        var horizon = Math.Max(60, new[] { Setting("LongWindowSeconds", 1800, 100000), Setting("LeaseSeconds", 120, 100000), Setting("CooldownSeconds", 10, 100000) }.Max());
        var cutoff = now.AddSeconds(-horizon - 300);
        var limit = Setting("CleanupBatchSize", 200, 1000); var removed = 0;
        var keys = await db.Set<CentReaderBudget>().AsNoTracking().Where(r => r.UpdatedAt < cutoff).OrderBy(r => r.UpdatedAt).Select(r => r.Key).Take(limit).ToArrayAsync(ct);
        foreach (var key in keys) {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // Same lock as retrieval/release. Recheck after acquiring it: a stale
            // candidate must never reset a newly used budget or an active lease.
            await new CentReaderProtection(db, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<CentReaderProtection>.Instance, clock).Lock([key], ct);
            var row = await db.Set<CentReaderBudget>().SingleOrDefaultAsync(r => r.Key == key, ct);
            if (row is not null) {
                await db.Entry(row).ReloadAsync(ct);
                var state = JsonSerializer.Deserialize<CentReaderProtection.State>(row.StateJson)!;
                if (row.UpdatedAt < cutoff && state.BlockedUntil <= now && state.Leases.All(l => l.Until <= now) && state.Requests.All(r => r.At <= now.AddSeconds(-horizon))) {
                    db.Remove(row); await db.SaveChangesAsync(ct); removed++;
                }
            }
            await tx.CommitAsync(ct);
        }
        // Audit deletion is deliberately opt-in, bounded, and restricted to
        // reader delivery events. Payment, notes, import and admin audits remain.
        if (int.TryParse(config["Cent:Reader:AuditRetentionDays"], out var days) && days is >= 30 and <= 3650) {
            var auditCutoff = now.AddDays(-days);
            var ids = await db.Audit.Where(a => a.CreatedAt < auditCutoff &&
                (a.Action == "READER_DOCUMENT_OPEN" || a.Action == "READER_SECTION_DELIVERY" || a.Action == "PILOT_SECTION_DELIVERY"))
                .OrderBy(a => a.CreatedAt).Select(a => a.Id).Take(limit).ToArrayAsync(ct);
            await db.Audit.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct);
        }
        return removed;
    }
}

public sealed class CentReaderCleanupWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<CentReaderCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue<bool>("Cent:Reader:Enabled") || string.Equals(config["Cent:Reader:Protection:CleanupEnabled"], "false", StringComparison.OrdinalIgnoreCase)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken)) {
                try { await using var scope = scopes.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<CentReaderCleanup>().Run(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { logger.LogWarning(error, "Cent reader bounded cleanup failed; retrying on next interval"); }
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
