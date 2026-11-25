using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cent.Module;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cent.Tests;

public class ReaderPilotPhase2Tests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static CentPilotProtection Guard(Clock clock, params (string, int)[] settings) => new(
        new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => "Cent:ReaderPilot:Protection:" + s.Item1, s => (string?)s.Item2.ToString())).Build(),
        NullLogger<CentPilotProtection>.Instance, clock);

    [Theory]
    [InlineData("AccountPerMinute")]
    [InlineData("IpPerMinute")]
    [InlineData("BurstPer10Seconds")]
    [InlineData("UniquePer10Seconds")]
    public void Bulk_retrieval_is_temporarily_throttled_and_recovers(string setting)
    {
        var clock = new Clock(); var guard = Guard(clock, (setting, 3)); var user = Guid.NewGuid(); var content = Guid.NewGuid();
        for (var i = 0; i < 3; i++) using (guard.Enter(user, "ip", setting.StartsWith("Unique") ? Guid.NewGuid() : content)) { }
        var error = Assert.Throws<CentException>(() => guard.Enter(user, "ip", setting.StartsWith("Unique") ? Guid.NewGuid() : content));
        Assert.Equal(429, error.Status); Assert.InRange(error.RetryAfterSeconds!.Value, 1, 60);
        clock.Now += TimeSpan.FromMinutes(1.1);
        using (guard.Enter(user, "ip", content)) { }
    }
    [Fact]
    public void Account_and_ip_concurrency_are_independent_and_leases_release()
    {
        var clock = new Clock(); var guard = Guard(clock, ("AccountConcurrent", 1), ("IpConcurrent", 2)); var user = Guid.NewGuid();
        var first = guard.Enter(user, "shared", Guid.NewGuid());
        Assert.Equal(429, Assert.Throws<CentException>(() => guard.Enter(user, "different", Guid.NewGuid())).Status);
        var second = guard.Enter(Guid.NewGuid(), "shared", Guid.NewGuid());
        Assert.Equal(429, Assert.Throws<CentException>(() => guard.Enter(Guid.NewGuid(), "shared", Guid.NewGuid())).Status);
        first.Dispose(); first.Dispose(); second.Dispose(); clock.Now += TimeSpan.FromSeconds(11);
        using (guard.Enter(user, "shared", Guid.NewGuid())) { }
    }
    [Fact]
    public void Normal_navigation_is_not_throttled()
    {
        var clock = new Clock(); var guard = Guard(clock); var user = Guid.NewGuid();
        for (int i = 0; i < 100; i++) { using (guard.Enter(user, "ip", Guid.NewGuid())) { } clock.Now += TimeSpan.FromSeconds(15); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Section_delivery_checks_entitlements_closes_bulk_path_and_maps_private_trace(bool enabled)
    {
        using var factory = new CentFactory(environmentVariables: new Dictionary<string, string?> {
            ["Cent__ReaderPilot__Enabled"] = enabled.ToString(), ["Cent__ReaderPilot__Protection__BurstPer10Seconds"] = "4"
        });
        using var client = factory.CreateClient();
        var auth = await (await TestAccounts.Create(factory, "pilot-private@example.com")).Content.ReadFromJsonAsync<JsonElement>();
        var user = auth.GetProperty("user").GetProperty("id").GetGuid();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.GetProperty("token").GetString());
        var training = new CentTraining { Title = "Math", Published = true };
        var course = new CentCourse { Code = "math", TrainingId = training.Id };
        var source = new CentSource { Id = Guid.NewGuid(), ImportKey = "math/chapter2.html", CourseCode = "math" };
        var document = new CentDocument { TrainingId = training.Id, SourceId = source.Id, Title = "Chapter", PageCount = 2, Published = true };
        var section = new CentSourceSection { Id = Guid.NewGuid(), SourceId = source.Id, ImportKey = CentReaderPilot.SectionKey, Heading = "Linear equations", SortOrder = 1, Text = "private linear body" };
        var sibling = new CentSourceSection { Id = Guid.NewGuid(), SourceId = source.Id, ImportKey = "math/chapter2.html#section-03", Heading = "Next", SortOrder = 2, Text = "private sibling body" };
        var product = new CentProduct { Title = "Test", PriceRial = 10000, Kind = "TRAINING", TrainingId = training.Id };
        var order = new CentOrder { UserId = user, ProductId = product.Id, AmountRial = 10000, TrainingId = training.Id, Kind = "TRAINING" };
        var grant = new CentEntitlement { UserId = user, OrderId = order.Id, TrainingId = training.Id, Kind = "TRAINING", StartsAt = DateTimeOffset.UtcNow.AddDays(-1), ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        using (var scope = factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>(); db.AddRange(training, course, source, document, section, sibling, product, order, grant); await db.SaveChangesAsync();
        }
        var path = $"/api/cent/v1/documents/{document.Id}";
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path + $"/source-sections/{section.Id}")).StatusCode);
        var outline = await client.GetStringAsync(path + "/source-sections");
        if (!enabled) {
            Assert.Contains("private linear body", outline); Assert.Contains("private sibling body", outline);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + $"/source-sections/{section.Id}")).StatusCode);
            return;
        }
        Assert.DoesNotContain("private linear body", outline); Assert.DoesNotContain("private sibling body", outline); Assert.DoesNotContain("pilotHtml", outline);
        var metadata = await client.GetStringAsync(path);
        Assert.DoesNotContain("pilot-private@example.com", metadata); Assert.DoesNotContain("private linear body", metadata);
        var response = await client.GetAsync(path + $"/source-sections/{section.Id}"); response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CentReaderPilot.Html(), body.GetProperty("pilotHtml").GetString());
        Assert.DoesNotContain("private sibling body", body.ToString());
        var trace = Guid.ParseExact(body.GetProperty("watermark").GetString()![3..], "N");
        using (var scope = factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
            var audit = await db.Audit.SingleAsync(a => a.Id == trace); Assert.Equal(user, audit.ActorId); Assert.Equal(section.Id, audit.EntityId);
        }
        var neighbour = await client.GetFromJsonAsync<JsonElement>(path + $"/source-sections/{sibling.Id}");
        Assert.Equal("private sibling body", neighbour.GetProperty("text").GetString()); Assert.Equal(JsonValueKind.Null, neighbour.GetProperty("pilotHtml").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/pages/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + $"/source-sections/{Guid.NewGuid()}")).StatusCode);
        for (var i = 0; i < 1; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path + $"/source-sections/{section.Id}")).StatusCode);
        var blocked = await client.GetAsync(path + $"/source-sections/{section.Id}"); Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode); Assert.NotNull(blocked.Headers.RetryAfter);
        // Expiry is checked before throttle state, on every body request and the old outline route.
        using (var scope = factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>(); var row = await db.Entitlements.SingleAsync(e => e.Id == grant.Id); row.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path + $"/source-sections/{section.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path + "/source-sections")).StatusCode);
    }
}
