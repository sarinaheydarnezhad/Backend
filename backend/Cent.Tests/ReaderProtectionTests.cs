using Cent.Module;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cent.Tests;

public class ReaderProtectionTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static CentReaderProtection Guard(CentDbContext db, Clock clock, params (string Key, int Value)[] settings) => new(db,
        new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => "Cent:Reader:Protection:" + s.Key, s => (string?)s.Value.ToString())).Build(),
        NullLogger<CentReaderProtection>.Instance, clock);
    private static SqliteCentContext Context(SqliteConnection connection) => new(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);

    [Theory]
    [InlineData("AccountPerMinute", 1)]
    [InlineData("IpPerMinute", 1)]
    [InlineData("BurstPer10Seconds", 1)]
    [InlineData("UniquePer10Seconds", 1)]
    [InlineData("AccountLongRequests", 1)]
    [InlineData("IpLongRequests", 1)]
    [InlineData("UniquePerLongWindow", 1)]
    public async Task Limits_survive_new_service_instances_and_recover(string setting, int limit)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using (var setup = Context(connection)) await setup.Database.EnsureCreatedAsync();
        var clock = new Clock(); var user = Guid.NewGuid();
        await using (var db = Context(connection)) await using (await Guard(db, clock, (setting, limit)).Enter(user, "ip", Guid.NewGuid(), false, default)) { }
        await using (var db = Context(connection)) {
            var error = await Assert.ThrowsAsync<CentException>(() => Guard(db, clock, (setting, limit)).Enter(user, "ip", Guid.NewGuid(), false, default));
            Assert.Equal(429, error.Status); Assert.True(error.RetryAfterSeconds > 0);
        }
        clock.Now += TimeSpan.FromSeconds(1801);
        await using (var db = Context(connection)) await using (await Guard(db, clock, (setting, limit)).Enter(user, "ip", Guid.NewGuid(), false, default)) { }
    }

    [Fact]
    public async Task Concurrency_is_shared_and_disposal_is_idempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var first = Context(connection); await first.Database.EnsureCreatedAsync();
        await using var second = Context(connection); var clock = new Clock(); var user = Guid.NewGuid();
        var lease = await Guard(first, clock, ("AccountConcurrent", 1)).Enter(user, "ip", Guid.NewGuid(), false, default);
        Assert.Equal(429, (await Assert.ThrowsAsync<CentException>(() => Guard(second, clock, ("AccountConcurrent", 1)).Enter(user, "other-ip", Guid.NewGuid(), false, default))).Status);
        await lease.DisposeAsync(); await lease.DisposeAsync(); clock.Now += TimeSpan.FromSeconds(11);
        await using var recovered = await Guard(second, clock, ("AccountConcurrent", 1)).Enter(user, "other-ip", Guid.NewGuid(), false, default);
    }

    [Fact]
    public async Task Slow_enumeration_is_detected_without_penalizing_normal_navigation_or_assets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Context(connection); await db.Database.EnsureCreatedAsync(); var clock = new Clock(); var user = Guid.NewGuid();
        var guard = Guard(db, clock, ("UniquePerLongWindow", 3));
        for (var i = 0; i < 3; i++) { await using (await guard.Enter(user, "ip", Guid.NewGuid(), false, default)) { } clock.Now += TimeSpan.FromSeconds(20); }
        Assert.Equal(429, (await Assert.ThrowsAsync<CentException>(() => guard.Enter(user, "ip", Guid.NewGuid(), false, default))).Status);
        for (var i = 0; i < 20; i++) await using (await guard.Enter(user, "ip", Guid.NewGuid(), true, default)) { }
        clock.Now += TimeSpan.FromMinutes(31);
        for (var i = 0; i < 100; i++) { await using (await guard.Enter(user, "ip", Guid.Empty, false, default)) { } clock.Now += TimeSpan.FromSeconds(15); }
    }
}
