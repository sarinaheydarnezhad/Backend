using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cent.Module;

// Only used by the opt-in pilot chapter's section delivery. No global limiter changes.
public sealed class CentPilotProtection(IConfiguration config, ILogger<CentPilotProtection> logger, TimeProvider clock)
{
    private sealed class Bucket
    {
        public readonly Queue<(DateTimeOffset At, Guid Content)> Requests = new();
        public int Active;
        public DateTimeOffset BlockedUntil;
        public DateTimeOffset Last;
    }
    private readonly Dictionary<string, Bucket> buckets = new();
    private readonly object gate = new();
    private int Setting(string key, int fallback) => int.TryParse(config["Cent:ReaderPilot:Protection:" + key], out var n) ? Math.Clamp(n, 1, 10000) : fallback;
    public IDisposable Enter(Guid account, string ip, Guid content)
    {
        lock (gate) {
            var now = clock.GetUtcNow();
            foreach (var key in buckets.Where(b => b.Value.Active == 0 && b.Value.Last < now.AddMinutes(-10)).Select(b => b.Key).ToArray()) buckets.Remove(key);
            var keys = new[] { "account:" + account, "ip:" + CentModule.Hash(ip) };
            if (buckets.Count > 20000) throw new CentException(429, "pilot_busy", "Please retry shortly.", 10);
            var selected = keys.Select(key => {
                if (!buckets.TryGetValue(key, out var b)) buckets[key] = b = new Bucket();
                b.Last = now;
                while (b.Requests.TryPeek(out var item) && item.At <= now.AddMinutes(-1)) b.Requests.Dequeue();
                return b;
            }).ToArray();
            for (var i = 0; i < selected.Length; i++) {
                var b = selected[i]; var recent = b.Requests.Where(r => r.At > now.AddSeconds(-10)).ToArray();
                var reason = b.BlockedUntil > now ? "cooldown" :
                    b.Active >= Setting(i == 0 ? "AccountConcurrent" : "IpConcurrent", i == 0 ? 2 : 8) ? "concurrency" :
                    b.Requests.Count >= Setting(i == 0 ? "AccountPerMinute" : "IpPerMinute", i == 0 ? 60 : 240) ? "rate" :
                    i == 0 && recent.Length >= Setting("BurstPer10Seconds", 12) ? "speed" :
                    i == 0 && recent.Select(r => r.Content).Append(content).Distinct().Count() > Setting("UniquePer10Seconds", 12) ? "enumeration" : null;
                if (reason is null) continue;
                if (reason != "cooldown") {
                    b.BlockedUntil = now.AddSeconds(Setting("CooldownSeconds", 10));
                    if (reason == "rate" && b.Requests.TryPeek(out var oldest) && oldest.At.AddMinutes(1) > b.BlockedUntil)
                        b.BlockedUntil = oldest.At.AddMinutes(1);
                }
                var retry = Math.Max(1, (int)Math.Ceiling((b.BlockedUntil - now).TotalSeconds));
                logger.LogWarning("Pilot security throttle {Reason}; account {Account}; ipHash {IpHash}; content {Content}; retry {Retry}", reason, account, CentModule.Hash(ip)[..16], content, retry);
                throw new CentException(429, "pilot_throttled", "Please retry shortly.", retry);
            }
            foreach (var b in selected) { b.Active++; b.Requests.Enqueue((now, content)); }
            return new Lease(() => { lock (gate) foreach (var b in selected) b.Active--; });
        }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}
