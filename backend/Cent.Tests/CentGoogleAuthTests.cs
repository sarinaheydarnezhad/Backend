using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cent.Module;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// The shared Cent factory temporarily sets process environment variables while starting a host.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Cent.Tests;

public sealed class CentGoogleAuthTests
{
    [Fact]
    public async Task Registration_and_repeat_login_use_Cent_sessions_and_subject_even_after_email_changes()
    {
        var google = new FakeGoogle(); using var host = Host(google); using var client = host.CreateClient();
        var start = await Start(client);
        var query = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query);
        Assert.Equal("code", query["response_type"]); Assert.Equal("openid email profile", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(start.CodeVerifier))), query["code_challenge"]);
        Assert.Equal("https://cent.example/cent-s/auth/google/callback", query["redirect_uri"]);
        using (var scope = host.Services.CreateScope()) {
            var attempt = await scope.ServiceProvider.GetRequiredService<CentDbContext>().GoogleAttempts.SingleAsync();
            Assert.Equal(CentModule.Hash(start.State), attempt.StateHash);
            Assert.Equal(CentModule.Hash(start.CodeVerifier), attempt.VerifierHash);
            Assert.Equal(CentModule.Hash(query["nonce"].ToString()), attempt.NonceHash);
        }
        var result = await Complete(client, start);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode); Assert.True(result.Headers.CacheControl!.NoStore);
        var auth = await result.Content.ReadFromJsonAsync<JsonElement>();
        var userId = auth.GetProperty("user").GetProperty("id").GetGuid();
        Assert.Equal("STUDENT", auth.GetProperty("user").GetProperty("role").GetString());
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/cent/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/cent/v1/admin/trainings")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/cent/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/cent/v1/me")).StatusCode);
        google.Identity = google.Identity with { Email = "changed@example.com" };
        var second = await (await Complete(client, await Start(client))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(userId, second.GetProperty("user").GetProperty("id").GetGuid());
        using var dbScope = host.Services.CreateScope(); var db = dbScope.ServiceProvider.GetRequiredService<CentDbContext>();
        var user = await db.Users.SingleAsync(); Assert.Equal("google-subject-123", user.GoogleSubject); Assert.Empty(user.PasswordHash);
        Assert.Equal(2, await db.Sessions.CountAsync()); Assert.Empty(await db.GoogleAttempts.ToListAsync());
        Assert.All(await db.Sessions.ToListAsync(), s => Assert.NotEqual(auth.GetProperty("token").GetString(), s.TokenHash));
    }

    [Fact]
    public async Task Matching_email_never_links_password_or_other_Google_accounts()
    {
        var google = new FakeGoogle(); using var host = Host(google); using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/cent/v1/auth/register", new {
            email = google.Identity.Email, password = "existing-password", name = "Existing"
        })).StatusCode);
        var response = await Complete(client, await Start(client));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("google_account_conflict", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using (var scope = host.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>(); var user = await db.Users.SingleAsync();
            Assert.Null(user.GoogleSubject); Assert.Single(await db.Sessions.ToListAsync());
            user.GoogleSubject = "different-google-subject"; await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await Complete(client, await Start(client))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/cent/v1/auth/login", new {
            email = google.Identity.Email, password = "existing-password"
        })).StatusCode);
    }

    [Fact]
    public async Task Invalid_state_or_browser_verifier_does_not_consume_real_attempt_and_replay_fails()
    {
        var google = new FakeGoogle(); using var host = Host(google); using var client = host.CreateClient(); var start = await Start(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Complete(client, start with { State = new string('x', 43) })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Complete(client, start with { CodeVerifier = new string('x', 43) })).StatusCode);
        Assert.Equal(0, google.Calls);
        Assert.Equal(HttpStatusCode.OK, (await Complete(client, start)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Complete(client, start)).StatusCode); Assert.Equal(1, google.Calls);
    }

    [Theory]
    [InlineData("access_denied", "google_cancelled")]
    [InlineData("server_error", "google_authorization_failed")]
    public async Task Cancellation_and_provider_errors_consume_attempt_without_login(string error, string code)
    {
        var google = new FakeGoogle(); using var host = Host(google); using var client = host.CreateClient(); var start = await Start(client);
        var response = await client.PostAsJsonAsync("/api/cent/v1/auth/google/complete", new { start.State, start.CodeVerifier, error });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Complete(client, start)).StatusCode); Assert.Equal(0, google.Calls);
    }

    [Fact]
    public async Task Concurrent_callback_is_rejected_while_first_exchange_is_in_progress()
    {
        var google = new FakeGoogle { HoldExchange = true }; using var host = Host(google); using var client = host.CreateClient();
        var start = await Start(client); var first = Complete(client, start);
        await google.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.Equal(HttpStatusCode.Unauthorized, (await Complete(client, start)).StatusCode); }
        finally { google.Release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode); Assert.Equal(1, google.Calls);
    }

    [Fact]
    public async Task Expired_attempt_and_failed_exchange_cannot_start_sessions()
    {
        var google = new FakeGoogle(); using var host = Host(google); using var client = host.CreateClient(); var start = await Start(client);
        using (var scope = host.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
            (await db.GoogleAttempts.SingleAsync()).ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Complete(client, start)).StatusCode); Assert.Equal(0, google.Calls);
        google.Fail = true; start = await Start(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Complete(client, start)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Complete(client, start)).StatusCode);
        using var dbScope = host.Services.CreateScope(); var database = dbScope.ServiceProvider.GetRequiredService<CentDbContext>();
        Assert.Empty(await database.Users.ToListAsync()); Assert.Empty(await database.Sessions.ToListAsync());
    }

    [Fact]
    public async Task Google_only_user_cannot_login_with_dummy_or_empty_password_and_email_registration_conflicts()
    {
        using var host = Host(new FakeGoogle()); using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Complete(client, await Start(client))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/cent/v1/auth/login", new {
            email = "google@example.com", password = "Cent-unknown-account-dummy"
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/cent/v1/auth/register", new {
            email = "google@example.com", password = "valid-password", name = "Student"
        })).StatusCode);
    }

    [Fact]
    public async Task Disabled_or_missing_configuration_is_unavailable_and_untrusted_callback_is_rejected()
    {
        using var host = Host(new FakeGoogle()); using var client = host.CreateClient();
        var config = host.Services.GetRequiredService<IConfiguration>();
        config["Cent:Google:Enabled"] = "false";
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/cent/v1/auth/google/start", null)).StatusCode);
        config["Cent:Google:Enabled"] = "true"; config["Cent:Google:RedirectUri"] = "https://attacker.example/callback";
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/cent/v1/auth/google/start", null)).StatusCode);
        config["Cent:Google:RedirectUri"] = "https://cent.example/callback"; config["Cent:Google:ClientSecret"] = "";
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/cent/v1/auth/google/start", null)).StatusCode);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.com")]
    [InlineData("https://cent.example/cent-s")]
    [InlineData("http://cent.example")]
    [InlineData("https://user:pass@cent.example")]
    public void Cors_rejects_wildcards_paths_nonlocal_HTTP_and_userinfo(string origin) =>
        Assert.Throws<InvalidOperationException>(() => CentGoogleSettings.ValidateOrigin(origin));

    [Theory]
    [InlineData("nonce")]
    [InlineData("iss")]
    [InlineData("aud")]
    [InlineData("azp")]
    [InlineData("exp")]
    [InlineData("iat")]
    [InlineData("nbf")]
    public void Additional_claim_checks_reject_wrong_nonce_presenter_expiry_and_future_times(string field)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object> { ["iss"] = "https://accounts.google.com", ["aud"] = "test-client", ["nonce"] = "expected", ["azp"] = "test-client", ["exp"] = now + 300, ["iat"] = now - 10 };
        CentGoogleClient.ValidateAdditionalClaims(JsonSerializer.SerializeToElement(claims), "test-client", CentModule.Hash("expected"));
        claims[field] = field switch { "nonce" => "wrong", "azp" => "wrong", "iss" => "https://attacker.example", "aud" => "other-client", "exp" => now - 10, _ => now + 1000 };
        Assert.Throws<CentException>(() => CentGoogleClient.ValidateAdditionalClaims(JsonSerializer.SerializeToElement(claims), "test-client", CentModule.Hash("expected")));
        claims[field] = new object();
        Assert.Throws<CentException>(() => CentGoogleClient.ValidateAdditionalClaims(JsonSerializer.SerializeToElement(claims), "test-client", CentModule.Hash("expected")));
    }

    [Fact]
    public async Task Official_validator_rejects_unsigned_and_malformed_tokens()
    {
        foreach (var token in new[] { "bad-token", "eyJhbGciOiJub25lIn0.eyJpc3MiOiJodHRwczovL2FjY291bnRzLmdvb2dsZS5jb20ifQ.", "", new string('a', 10001) })
            Assert.Equal(401, (await Assert.ThrowsAsync<CentException>(() => CentGoogleClient.ValidateToken(token, "test-client", "nonce-hash", default))).Status);
    }

    [Theory]
    [InlineData(400, 401)]
    [InlineData(429, 503)]
    [InlineData(503, 503)]
    [InlineData(200, 401)]
    public async Task Token_exchange_sends_fixed_credentials_redirect_and_PKCE_and_safely_handles_errors(int status, int expected)
    {
        var handler = new TokenHandler(status); using var http = new HttpClient(handler);
        var google = new CentGoogleClient(http);
        var settings = new CentGoogleSettings("test-client", "test-only-secret", "https://cent.example/callback");
        var error = await Assert.ThrowsAsync<CentException>(() => google.ExchangeAndValidate(settings, "test-code", "test-verifier", "nonce-hash", default));
        Assert.Equal(expected, error.Status); Assert.DoesNotContain("test-only-secret", error.Message);
        Assert.Equal("https://oauth2.googleapis.com/token", handler.Url);
        var body = QueryHelpers.ParseQuery(handler.Body!);
        Assert.Equal("authorization_code", body["grant_type"]); Assert.Equal("test-code", body["code"]);
        Assert.Equal("test-verifier", body["code_verifier"]); Assert.Equal(settings.ClientId, body["client_id"]);
        Assert.Equal(settings.ClientSecret, body["client_secret"]); Assert.Equal(settings.RedirectUri, body["redirect_uri"]);
        Assert.DoesNotContain(settings.ClientSecret, settings.ToString());
    }

    [Fact]
    public async Task Database_constraints_prevent_duplicate_subjects_and_email_and_migration_only_changes_Cent()
    {
        using var host = Host(new FakeGoogle()); using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
        db.Users.Add(new CentUser { Email = "one@example.com", NormalizedEmail = "ONE@EXAMPLE.COM", GoogleSubject = "same-subject" });
        await db.SaveChangesAsync();
        db.Users.Add(new CentUser { Email = "two@example.com", NormalizedEmail = "TWO@EXAMPLE.COM", GoogleSubject = "same-subject" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        db.Users.Add(new CentUser { Email = "one@example.com", NormalizedEmail = "ONE@EXAMPLE.COM", GoogleSubject = "other-subject" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        using var sqlDb = new CentDbContextFactory().CreateDbContext([]);
        var migrator = sqlDb.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        var script = migrator.GenerateScript("20261107130614_Curriculum", "20261109184203_AddCentGoogleAuthentication",
            Microsoft.EntityFrameworkCore.Migrations.MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("ALTER TABLE [cent_users] ADD [GoogleSubject]", script);
        Assert.Contains("Latin1_General_100_BIN2", script); Assert.Contains("CREATE UNIQUE INDEX [IX_cent_users_GoogleSubject]", script);
        Assert.DoesNotContain("__EFMigrationsHistory", script); Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("ALTER TABLE [Users]", script);
        Assert.False(sqlDb.Database.HasPendingModelChanges());
    }

    private static CentFactory Host(FakeGoogle google)
    {
        var host = new CentFactory(googleClient: google);
        var config = host.Services.GetRequiredService<IConfiguration>();
        config["Cent:Google:Enabled"] = "true"; config["Cent:Google:ClientId"] = "test-client";
        config["Cent:Google:ClientSecret"] = "test-only-not-a-real-secret";
        config["Cent:Google:RedirectUri"] = "https://cent.example/cent-s/auth/google/callback";
        return host;
    }

    private static async Task<CentGoogleStart> Start(HttpClient client)
    {
        var response = await client.PostAsync("/api/cent/v1/auth/google/start", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CentGoogleStart>())!;
    }
    private static Task<HttpResponseMessage> Complete(HttpClient client, CentGoogleStart start) =>
        client.PostAsJsonAsync("/api/cent/v1/auth/google/complete", new { start.State, start.CodeVerifier, code = "test-authorization-code" });

    private sealed class FakeGoogle : ICentGoogleClient
    {
        public CentGoogleIdentity Identity { get; set; } = new("google-subject-123", "google@example.com", "Google Student");
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public bool HoldExchange { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CentGoogleIdentity> ExchangeAndValidate(CentGoogleSettings settings, string code, string verifier, string nonceHash, CancellationToken ct)
        {
            Calls++; Entered.TrySetResult(); if (HoldExchange) await Release.Task.WaitAsync(ct);
            if (Fail) throw CentGoogleSettings.Invalid(); return Identity;
        }
    }

    private sealed class TokenHandler(int status) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Url { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri!.AbsoluteUri; Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = JsonContent.Create(new { error = "provider-private-detail" }) };
        }
    }
}
