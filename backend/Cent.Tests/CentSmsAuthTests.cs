using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cent.Module;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cent.Tests;

public sealed class CentSmsAuthTests
{
    [Fact]
    public void Sms_registration_preserves_the_host_clock_and_disabled_Cent_registers_nothing()
    {
        var hostClock = new TestTime();
        var services = new ServiceCollection(); services.AddSingleton<TimeProvider>(hostClock);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Cent:Enabled"] = "true" }).Build();
        services.AddCentModule(config);
        using var provider = services.BuildServiceProvider();
        Assert.Same(hostClock, provider.GetRequiredService<TimeProvider>());
        Assert.NotSame(hostClock, provider.GetRequiredService<CentSmsClock>());
        var disabled = new ServiceCollection(); disabled.AddCentModule(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.DoesNotContain(disabled, d => d.ServiceType == typeof(CentSmsClock) || d.ServiceType == typeof(ICentSmsSender));
    }
    [Theory]
    [InlineData("09123456789")]
    [InlineData("989123456789")]
    [InlineData("+989123456789")]
    [InlineData("۰۹۱۲۳۴۵۶۷۸۹")]
    public void Iranian_numbers_are_canonical(string value) => Assert.Equal("+989123456789", CentIranPhone.Normalize(value));

    [Theory]
    [InlineData("")]
    [InlineData("+98912345678")]
    [InlineData("08123456789")]
    [InlineData("+12025551234")]
    [InlineData("09123456789\n123")]
    public void Invalid_numbers_are_rejected(string value) => Assert.Equal("invalid_phone", Assert.Throws<CentException>(() => CentIranPhone.Normalize(value)).Code);

    [Fact]
    public async Task Request_verification_returning_user_session_logout_and_replay()
    {
        using var host = new SmsHost();
        var challenge = await host.Request();
        Assert.Equal(6, host.Sender.Code.Length);
        Assert.Equal(TimeSpan.FromMinutes(2), challenge.ExpiresAt - host.Clock.GetUtcNow());
        using (var scope = host.App.Services.CreateScope()) {
            var row = await scope.ServiceProvider.GetRequiredService<CentDbContext>().Set<CentSmsChallenge>().SingleAsync();
            Assert.NotEqual(host.Sender.Code, row.Verifier); Assert.NotEqual(challenge.ChallengeId, row.IdHash);
        }
        var session = await host.Verify(challenge);
        var user = session.GetProperty("user");
        var id = user.GetProperty("id").GetGuid();
        Assert.Equal(challenge.Phone, user.GetProperty("phone").GetString());
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", session.GetProperty("token").GetString());
        var profile = await host.Client.GetFromJsonAsync<JsonElement>("/api/cent/v1/me");
        Assert.Equal(challenge.Phone, profile.GetProperty("phone").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.VerifyResponse(challenge)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PostAsync("/api/cent/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/cent/v1/me")).StatusCode);
        host.Clock.Advance(61);
        var returning = await host.Verify(await host.Request());
        Assert.Equal(id, returning.GetProperty("user").GetProperty("id").GetGuid());
        using var final = host.App.Services.CreateScope(); var db = final.ServiceProvider.GetRequiredService<CentDbContext>();
        Assert.Single(await db.Users.ToListAsync()); Assert.Single(await db.Set<CentMobileIdentity>().ToListAsync());
    }

    [Fact]
    public async Task Wrong_codes_spend_five_attempts_and_resend_supersedes_old_challenges()
    {
        using var host = new SmsHost(); var challenge = await host.Request();
        var wrong = host.Sender.Code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await host.VerifyResponse(challenge, wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.VerifyResponse(challenge)).StatusCode);
        // Existing auth IP limiter counts request+verification calls; use services for the rest.
        host.Clock.Advance(61); var next = await host.RequestService();
        Assert.Equal(401, (await Assert.ThrowsAsync<CentException>(() => host.VerifyService(challenge))).Status);
        await host.VerifyService(next);
    }

    [Fact]
    public async Task Expired_missing_and_undelivered_challenges_are_rejected()
    {
        using var host = new SmsHost(); var challenge = await host.RequestService(); host.Clock.Advance(121);
        Assert.Equal(401, (await Assert.ThrowsAsync<CentException>(() => host.VerifyService(challenge))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<CentException>(() => host.VerifyService(challenge with { ChallengeId = new string('A', 64) }))).Status);
        var next = await host.RequestService();
        using (var scope = host.App.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<CentDbContext>().Set<CentSmsChallenge>().ExecuteUpdateAsync(u => u.SetProperty(c => c.Delivered, false));
        Assert.Equal(401, (await Assert.ThrowsAsync<CentException>(() => host.VerifyService(next))).Status);
    }

    [Fact]
    public async Task Cooldown_and_durable_phone_ip_and_global_limits_prevent_provider_abuse()
    {
        using var host = new SmsHost(); await host.RequestService();
        Assert.Equal(429, (await Assert.ThrowsAsync<CentException>(() => host.RequestService())).Status);
        Assert.Equal(1, host.Sender.Calls);
        for (var i = 1; i < 5; i++) { host.Clock.Advance(61); await host.RequestService(); }
        host.Clock.Advance(61);
        Assert.Equal(429, (await Assert.ThrowsAsync<CentException>(() => host.RequestService())).Status);
        Assert.Equal(5, host.Sender.Calls);
        host.Clock.Advance(3601); await host.RequestService();
        // All settings are Cent-only; separate hosts exercise remaining budget dimensions.
        using var ipHost = new SmsHost(new() { ["Cent__Sms__IpPerHour"] = "1" });
        await ipHost.RequestService();
        Assert.Equal(429, (await Assert.ThrowsAsync<CentException>(() => ipHost.RequestService("09123456780"))).Status);
        using var globalHost = new SmsHost(new() { ["Cent__Sms__GlobalPerMinute"] = "1" });
        await globalHost.RequestService();
        Assert.Equal(429, (await Assert.ThrowsAsync<CentException>(() => globalHost.RequestService("09123456780", "other-ip"))).Status);
    }

    [Fact]
    public async Task Provider_failure_invalidates_the_challenge_and_keeps_cooldown()
    {
        using var host = new SmsHost(); host.Sender.Fail = true;
        Assert.Equal(503, (await Assert.ThrowsAsync<CentException>(() => host.RequestService())).Status);
        using (var scope = host.App.Services.CreateScope()) {
            var row = await scope.ServiceProvider.GetRequiredService<CentDbContext>().Set<CentSmsChallenge>().SingleAsync();
            Assert.True(row.Consumed); Assert.False(row.Delivered);
        }
        host.Sender.Fail = false;
        Assert.Equal(429, (await Assert.ThrowsAsync<CentException>(() => host.RequestService())).Status);
    }

    [Fact]
    public async Task Authenticated_linking_binds_proof_to_owner_and_preserves_account_data()
    {
        using var host = new SmsHost();
        var seed = await (await TestAccounts.Create(host.App, "google-owner@example.com")).Content.ReadFromJsonAsync<JsonElement>();
        var id = seed.GetProperty("user").GetProperty("id").GetGuid();
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", seed.GetProperty("token").GetString());
        var originalProfile = await host.Client.GetFromJsonAsync<JsonElement>("/api/cent/v1/me");
        Assert.Equal(JsonValueKind.Null, originalProfile.GetProperty("phone").ValueKind);
        var challenge = await host.Request(link: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.VerifyResponse(challenge)).StatusCode);
        var linked = await host.Verify(challenge, link: true);
        Assert.Equal(id, linked.GetProperty("user").GetProperty("id").GetGuid());
        Assert.Equal(challenge.Phone, linked.GetProperty("user").GetProperty("phone").GetString());
        using var replacement = await host.Client.PostAsJsonAsync("/api/cent/v1/auth/identities/sms/request", new { phone = "09912345678" });
        Assert.Equal(HttpStatusCode.Conflict, replacement.StatusCode);
        Assert.Equal(1, host.Sender.Calls);
        host.Clock.Advance(61);
        var login = await host.Verify(await host.Request());
        Assert.Equal(id, login.GetProperty("user").GetProperty("id").GetGuid());
        var other = await (await TestAccounts.Create(host.App, "other@example.com")).Content.ReadFromJsonAsync<JsonElement>();
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", other.GetProperty("token").GetString());
        host.Clock.Advance(61); var conflict = await host.RequestService(target: other.GetProperty("user").GetProperty("id").GetGuid());
        Assert.Equal(409, (await Assert.ThrowsAsync<CentException>(() => host.VerifyService(conflict, target: other.GetProperty("user").GetProperty("id").GetGuid()))).Status);
        using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
        Assert.Equal(2, await db.Users.CountAsync()); Assert.Equal(id, (await db.Set<CentMobileIdentity>().SingleAsync()).UserId);
    }

    [Fact]
    public async Task Concurrent_verification_issues_at_most_one_session()
    {
        using var host = new SmsHost(fileDatabase: true); var challenge = await host.RequestService();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => {
            await gate.Task;
            try { await host.VerifyService(challenge); return true; }
            catch (CentException) { return false; }
            catch (Microsoft.Data.Sqlite.SqliteException) { return false; } // SQLite write contention fails closed.
        })).ToArray();
        gate.SetResult(); Assert.Equal(1, (await Task.WhenAll(results)).Count(x => x));
        using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
        Assert.Single(await db.Sessions.ToListAsync()); Assert.Single(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task Disabled_SMS_and_unauthenticated_linking_fail_closed()
    {
        using var host = new SmsHost(new() { ["Cent__Sms__Enabled"] = "false" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Client.PostAsJsonAsync("/api/cent/v1/auth/sms/request", new { phone = "09123456789" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync("/api/cent/v1/auth/identities/sms/request", new { phone = "09123456789" })).StatusCode);
        Assert.Equal(0, host.Sender.Calls);
    }

    [Fact]
    public async Task Password_registration_and_reset_preserve_identity_reject_cross_purpose_codes_and_revoke_sessions()
    {
        const string phone = "09123456789";
        const string initial = "Cent-one";
        const string replacement = "Cent-two";
        using var host = new SmsHost();
        var challenge = await host.Request();
        var loginCode = host.Sender.Code;
        Assert.Equal(CentSmsPurpose.SignIn, host.Sender.Purpose);
        using var wrongPurpose = await Reset(challenge, loginCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPurpose.StatusCode);

        using var register = await host.Client.PostAsJsonAsync("/api/cent/v1/auth/sms/verify",
            new { challenge.ChallengeId, code = loginCode, password = initial });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var original = await register.Content.ReadFromJsonAsync<JsonElement>();
        var originalId = original.GetProperty("user").GetProperty("id").GetGuid();
        using var signedIn = await Login(initial);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var passwordUser = (await signedIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user");
        Assert.Equal(challenge.Phone, passwordUser.GetProperty("phone").GetString());
        Assert.True(passwordUser.GetProperty("hasPassword").GetBoolean());

        host.Clock.Advance(61);
        using var request = await host.Client.PostAsJsonAsync("/api/cent/v1/auth/password/reset/request", new { phone });
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        var recovery = (await request.Content.ReadFromJsonAsync<CentSmsRequested>())!;
        Assert.Equal(CentSmsPurpose.PasswordReset, host.Sender.Purpose);
        using var recoveryAsLogin = await host.VerifyResponse(recovery);
        Assert.Equal(HttpStatusCode.Unauthorized, recoveryAsLogin.StatusCode);
        using var reset = await Reset(recovery, host.Sender.Code);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        using var replay = await Reset(recovery, host.Sender.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var oldLogin = await Login(initial);
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        using var profile = new HttpRequestMessage(HttpMethod.Get, "/api/cent/v1/me");
        profile.Headers.Authorization = new("Bearer", original.GetProperty("token").GetString());
        using var revoked = await host.Client.SendAsync(profile);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var newLogin = await Login(replacement);
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
        var restored = await newLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(originalId, restored.GetProperty("user").GetProperty("id").GetGuid());

        Task<HttpResponseMessage> Login(string password) =>
            host.Client.PostAsJsonAsync("/api/cent/v1/auth/password/login", new { phone, password });
        Task<HttpResponseMessage> Reset(CentSmsRequested sms, string code) =>
            host.Client.PostAsJsonAsync("/api/cent/v1/auth/password/reset/verify", new { sms.ChallengeId, code, password = replacement });
    }

    [Theory]
    [InlineData("/auth/sms")]
    [InlineData("/auth/password/reset")]
    public async Task Sms_password_setup_rejects_seven_characters(string path)
    {
        using var host = new SmsHost();
        using var request = await host.Client.PostAsJsonAsync("/api/cent/v1" + path + "/request", new { phone = "09123456789" });
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        var challenge = (await request.Content.ReadFromJsonAsync<CentSmsRequested>())!;
        using var response = await host.Client.PostAsJsonAsync("/api/cent/v1" + path + "/verify",
            new { challenge.ChallengeId, code = host.Sender.Code, password = "1234567" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_password", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Recovery_cannot_create_an_account()
    {
        using var host = new SmsHost();
        using var request = await host.Client.PostAsJsonAsync("/api/cent/v1/auth/password/reset/request", new { phone = "09123456789" });
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        var challenge = (await request.Content.ReadFromJsonAsync<CentSmsRequested>())!;
        using var response = await host.Client.PostAsJsonAsync("/api/cent/v1/auth/password/reset/verify",
            new { challenge.ChallengeId, code = host.Sender.Code, password = "a brand new password" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var scope = host.App.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<CentDbContext>().Users.AnyAsync());
    }

    private sealed class FakeSender : ICentSmsSender
    {
        public string Code = "";
        public int Calls;
        public bool Fail;
        public CentSmsPurpose Purpose;
        public Task Send(string phone, string code, CentSmsPurpose purpose, CancellationToken ct) {
            Calls++; Code = code; Purpose = purpose;
            if (Fail) throw CentMelipayamak.Unavailable();
            return Task.CompletedTask;
        }
    }
    private sealed class TestTime : CentSmsClock
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
    private sealed class SmsHost : IDisposable
    {
        private readonly CentFactory root;
        private readonly string file = Path.Combine(Path.GetTempPath(), "cent-sms-test-" + Guid.NewGuid().ToString("N") + ".db");
        public readonly FakeSender Sender = new();
        public readonly TestTime Clock = new();
        public WebApplicationFactory<Program> App { get; }
        public HttpClient Client { get; }
        public SmsHost(Dictionary<string, string?>? settings = null, bool fileDatabase = false)
        {
            settings ??= new(); settings.TryAdd("Cent__Sms__Enabled", "true");
            settings["Cent__Sms__VerifierKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x6b, 32).ToArray());
            root = new CentFactory(environmentVariables: settings);
            App = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                services.RemoveAll<ICentSmsSender>(); services.AddSingleton<ICentSmsSender>(Sender);
                services.RemoveAll<CentSmsClock>(); services.AddSingleton<CentSmsClock>(Clock);
                if (fileDatabase) {
                    services.RemoveAll<CentDbContext>(); services.RemoveAll<DbContextOptions<CentDbContext>>();
                    services.AddScoped<CentDbContext>(_ => new SqliteCentContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite("Data Source=" + file).Options));
                    using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
                    scope.ServiceProvider.GetRequiredService<CentDbContext>().Database.EnsureCreated();
                }
            }));
            Client = App.CreateClient();
        }
        public async Task<CentSmsRequested> Request(bool link = false) {
            using var response = await Client.PostAsJsonAsync(link ? "/api/cent/v1/auth/identities/sms/request" : "/api/cent/v1/auth/sms/request", new { phone = "09123456789" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<CentSmsRequested>())!;
        }
        public Task<HttpResponseMessage> VerifyResponse(CentSmsRequested challenge, string? code = null, bool link = false) => Client.PostAsJsonAsync(link ? "/api/cent/v1/auth/identities/sms/verify" : "/api/cent/v1/auth/sms/verify", new { challengeId = challenge.ChallengeId, code = code ?? Sender.Code });
        public async Task<JsonElement> Verify(CentSmsRequested challenge, bool link = false) {
            using var response = await VerifyResponse(challenge, link: link); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        public async Task<CentSmsRequested> RequestService(string phone = "09123456789", string ip = "test-ip", Guid? target = null) {
            using var scope = App.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<CentSmsAuth>().Request(phone, ip, target, default);
        }
        public async Task<object?> VerifyService(CentSmsRequested challenge, Guid? target = null) {
            using var scope = App.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<CentSmsAuth>().Verify(new(challenge.ChallengeId, Sender.Code), target, default);
        }
        public void Dispose() { Client.Dispose(); App.Dispose(); root.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }
}
