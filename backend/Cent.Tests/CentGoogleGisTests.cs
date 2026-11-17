using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Cent.Module;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Cent.Tests;

public sealed class CentGoogleGisTests
{
    private const string Root = "/api/cent/v1/auth/google";

    [Theory]
    [InlineData(true, "https://itatest.ir", "https://lerona-app.com")]
    [InlineData(false, "http://localhost:5173", "http://127.0.0.1:5108")]
    public async Task Real_signed_GIS_tokens_register_and_login_with_production_and_local_environment_settings(
        bool production, string origin, string backend)
    {
        using var host = new GisHost(production, origin, backend); var client = host.Client;
        var config = await (await client.GetAsync(Root + "/config")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(config.GetProperty("enabled").GetBoolean());
        Assert.Equal("GisIdToken", config.GetProperty("mode").GetString());
        Assert.Equal("test-web-client", config.GetProperty("clientId").GetString());
        using var preflight = new HttpRequestMessage(HttpMethod.Options, Root + "/gis/complete");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type,x-cent-gis");
        using var cors = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, cors.StatusCode);
        Assert.Equal(origin, cors.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(cors.Headers.Contains("Access-Control-Allow-Credentials"));

        var challenge = await host.Challenge();
        foreach (var random in new[] { challenge.ChallengeId, challenge.Nonce, challenge.BrowserBinding }) {
            Assert.Equal(43, random.Length); Assert.Equal(32, WebEncoders.Base64UrlDecode(random).Length);
        }
        Assert.InRange(challenge.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4.9), TimeSpan.FromMinutes(5));
        using (var scope = host.App.Services.CreateScope()) {
            var attempt = await scope.ServiceProvider.GetRequiredService<CentDbContext>().GoogleAttempts.SingleAsync();
            Assert.Equal(CentModule.Hash(challenge.Nonce), attempt.NonceHash);
            Assert.NotEqual(CentModule.Hash(challenge.ChallengeId), attempt.StateHash);
            Assert.DoesNotContain(challenge.BrowserBinding, JsonSerializer.Serialize(attempt));
        }
        using var first = await host.Complete(challenge);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl!.NoStore);
        var auth = await first.Content.ReadFromJsonAsync<JsonElement>();
        var userId = auth.GetProperty("user").GetProperty("id").GetGuid();
        var session = auth.GetProperty("token").GetString()!;
        Assert.Equal(64, session.Length); Assert.Equal("STUDENT", auth.GetProperty("user").GetProperty("role").GetString());
        client.DefaultRequestHeaders.Authorization = new("Bearer", session);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/cent/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        // The established GoogleSubject is authoritative even after Google's email changes.
        var second = await host.Challenge();
        using var login = await host.Complete(second, host.Token(second, c => c["email"] = "changed@example.com"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(userId, (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user").GetProperty("id").GetGuid());
        using (var scope = host.App.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
            var user = await db.Users.SingleAsync();
            Assert.Equal("google-gis-subject", user.GoogleSubject); Assert.Equal("gis@example.com", user.Email);
            Assert.Empty(user.PasswordHash); Assert.Equal(2, await db.Sessions.CountAsync());
            Assert.All(await db.Sessions.ToListAsync(), s => Assert.NotEqual(session, s.TokenHash));
            Assert.Empty(await db.GoogleAttempts.ToListAsync());
        }
        Assert.Equal(1, host.Keys.Calls); Assert.Equal(0, host.Exchange.Calls);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync(Root + "/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/cent/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/cent/v1/me")).StatusCode);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("aud")]
    [InlineData("iss")]
    [InlineData("exp")]
    [InlineData("iat")]
    [InlineData("missing-iat")]
    [InlineData("nbf")]
    [InlineData("nonce")]
    [InlineData("missing-nonce")]
    [InlineData("sub")]
    [InlineData("missing-sub")]
    [InlineData("email_verified")]
    [InlineData("email")]
    [InlineData("azp")]
    [InlineData("algorithm")]
    [InlineData("unsigned")]
    [InlineData("malformed")]
    [InlineData("duplicate-nonce")]
    public async Task Invalid_signed_tokens_never_create_accounts_or_sessions_and_consume_valid_challenge(string failure)
    {
        using var host = new GisHost(); var challenge = await host.Challenge();
        using var other = new SigningMaterial("other");
        string token;
        if (failure == "signature") token = host.Token(challenge, key: other.KeyWithId(host.Signing.KeyId));
        else if (failure == "algorithm") token = host.Token(challenge, algorithm: SecurityAlgorithms.RsaSha512);
        else if (failure == "unsigned") token = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0.";
        else if (failure == "malformed") token = "not-a-jwt";
        else if (failure == "duplicate-nonce") {
            var claims = host.Claims(challenge);
            var json = JsonSerializer.Serialize(claims);
            json = json[..^1] + ",\"nonce\":\"other\"}";
            token = SignRaw(json, host.Signing);
        }
        else token = host.Token(challenge, claims => {
            if (failure.StartsWith("missing-")) claims.Remove(failure[8..]);
            else claims[failure] = failure switch {
                "exp" => DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
                "iat" or "nbf" => DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
                "email_verified" => false, "sub" => "", "email" => "invalid-email", _ => "incorrect"
            };
        });
        await Error(await host.Complete(challenge, token), HttpStatusCode.Unauthorized, "invalid_google_credentials");
        await Error(await host.Complete(challenge), HttpStatusCode.Unauthorized, "invalid_google_credentials");
        await host.AssertNoAccounts(); Assert.Equal(0, host.Exchange.Calls);
    }

    [Fact]
    public async Task Unknown_expired_and_replayed_challenges_are_rejected()
    {
        using var host = new GisHost(); var challenge = await host.Challenge();
        var random = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await Error(await host.Complete(challenge with { ChallengeId = random }), HttpStatusCode.Unauthorized, "invalid_google_credentials");
        using (var scope = host.App.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
            (await db.GoogleAttempts.SingleAsync()).ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        }
        await Error(await host.Complete(challenge), HttpStatusCode.Unauthorized, "invalid_google_credentials");
        challenge = await host.Challenge();
        Assert.Equal(HttpStatusCode.OK, (await host.Complete(challenge)).StatusCode);
        await Error(await host.Complete(challenge), HttpStatusCode.Unauthorized, "invalid_google_credentials");
        Assert.Equal(1, host.Keys.Calls);
    }

    [Fact]
    public async Task Browser_proof_and_origin_are_both_bound_and_failures_do_not_consume_valid_challenge()
    {
        using var host = new GisHost(); var challenge = await host.Challenge();
        var random = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await Error(await host.Complete(challenge with { BrowserBinding = random }), HttpStatusCode.Unauthorized, "invalid_google_credentials");
        // This origin is allowed by CORS, but is not the origin that created the challenge.
        host.Client.DefaultRequestHeaders.Remove("Origin"); host.Client.DefaultRequestHeaders.Add("Origin", "https://second.example");
        await Error(await host.Complete(challenge), HttpStatusCode.Unauthorized, "invalid_google_credentials");
        host.Client.DefaultRequestHeaders.Remove("Origin"); host.Client.DefaultRequestHeaders.Add("Origin", "https://itatest.ir");
        Assert.Equal(0, host.Keys.Calls);
        Assert.Equal(HttpStatusCode.OK, (await host.Complete(challenge)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_completion_has_exactly_one_winner_before_key_fetch_finishes()
    {
        using var host = new GisHost(); var challenge = await host.Challenge();
        host.Keys.Hold = true;
        var first = host.Complete(challenge);
        await host.Keys.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { await Error(await host.Complete(challenge), HttpStatusCode.Unauthorized, "invalid_google_credentials"); }
        finally { host.Keys.Release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        using var scope = host.App.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<CentDbContext>().Sessions.ToListAsync());
        Assert.Equal(1, host.Keys.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Matching_email_never_links_password_or_other_Google_subjects_and_password_login_survives(bool linked)
    {
        using var host = new GisHost();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/cent/v1/auth/register", new {
            email = "gis@example.com", password = "existing-test-password", name = "Existing"
        })).StatusCode);
        if (linked) {
            using var scope = host.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
            (await db.Users.SingleAsync()).GoogleSubject = "another-google-subject"; await db.SaveChangesAsync();
        }
        await Error(await host.Complete(await host.Challenge()), HttpStatusCode.Conflict, "google_account_conflict");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/cent/v1/auth/login", new {
            email = "gis@example.com", password = "existing-test-password"
        })).StatusCode);
        using var finalScope = host.App.Services.CreateScope(); var database = finalScope.ServiceProvider.GetRequiredService<CentDbContext>();
        Assert.Single(await database.Users.ToListAsync()); Assert.Equal(2, await database.Sessions.CountAsync());
    }

    [Theory]
    [InlineData("missing-origin", 403, "google_origin_rejected")]
    [InlineData("untrusted-origin", 403, "google_origin_rejected")]
    [InlineData("missing-header", 403, "google_origin_rejected")]
    [InlineData("form", 415, "unsupported_media_type")]
    [InlineData("large", 413, "request_too_large")]
    [InlineData("large-stream", 413, "request_too_large")]
    [InlineData("malformed", 400, "invalid_request")]
    [InlineData("null", 400, "invalid_request")]
    [InlineData("extra", 400, "invalid_request")]
    [InlineData("duplicate", 400, "invalid_request")]
    public async Task Origin_CSRF_and_bounded_JSON_are_enforced(string failure, int status, string code)
    {
        using var host = new GisHost();
        if (failure is "missing-origin" or "untrusted-origin") host.Client.DefaultRequestHeaders.Remove("Origin");
        if (failure == "untrusted-origin") host.Client.DefaultRequestHeaders.Add("Origin", "https://attacker.example");
        if (failure == "missing-header") host.Client.DefaultRequestHeaders.Remove("X-Cent-GIS");
        var body = failure switch {
            "large" or "large-stream" => new string(' ', 257), "malformed" => "{",
            "extra" => "{\"extra\":true}", "null" => "null", "duplicate" => "{\"x\":1,\"x\":2}", _ => "{}"
        };
        using HttpContent content = failure == "large-stream" ? new UnknownLengthContent(body)
            : new StringContent(body, Encoding.UTF8, failure == "form" ? "application/x-www-form-urlencoded" : "application/json");
        await Error(await host.Client.PostAsync(Root + "/gis/challenge", content), (HttpStatusCode)status, code);
        Assert.Equal(0, host.Keys.Calls); await host.AssertNoAccounts();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"challengeId\":\"x\",\"browserBinding\":\"y\",\"idToken\":\"z\"}")]
    [InlineData("{\"challengeId\":null,\"browserBinding\":null,\"idToken\":null}")]
    [InlineData("{\"extra\":true}")]
    public async Task Completion_requires_exact_fields_and_formats(string json)
    {
        using var host = new GisHost();
        await Error(await host.Client.PostAsync(Root + "/gis/complete", new StringContent(json, Encoding.UTF8, "application/json")),
            HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, host.Keys.Calls);
    }

    [Fact]
    public async Task GIS_is_opt_in_and_invalid_configuration_fails_closed_without_requiring_OAuth_secret_or_callback()
    {
        using var host = new GisHost();
        var config = host.App.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        foreach (var pair in new[] { ("Mode", "AuthorizationCode"), ("Mode", "typo"), ("Enabled", "false"), ("ClientId", "") }) {
            config["Cent:Google:" + pair.Item1] = pair.Item2;
            await Error(await host.Client.PostAsJsonAsync(Root + "/gis/challenge", new { }), HttpStatusCode.ServiceUnavailable, "google_unavailable");
            config["Cent:Google:Mode"] = "GisIdToken"; config["Cent:Google:Enabled"] = "true"; config["Cent:Google:ClientId"] = "test-web-client";
        }
        Assert.NotNull(await host.Challenge());
        using var scope = host.App.Services.CreateScope(); Assert.Single(await scope.ServiceProvider.GetRequiredService<CentDbContext>().GoogleAttempts.ToListAsync());
        config["Cent:Google:Enabled"] = "false";
        var discovery = await (await host.Client.GetAsync(Root + "/config")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(discovery.GetProperty("enabled").GetBoolean()); Assert.Equal(JsonValueKind.Null, discovery.GetProperty("clientId").ValueKind);
    }

    [Fact]
    public async Task Production_rejects_loopback_HTTP_origins_and_GIS_obeys_existing_auth_rate_limit()
    {
        using (var host = new GisHost(true, "http://localhost:5173"))
            await Error(await host.Client.PostAsJsonAsync(Root + "/gis/challenge", new { }), HttpStatusCode.Forbidden, "google_origin_rejected");
        using var limited = new GisHost();
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.OK, (await limited.Client.PostAsJsonAsync(Root + "/gis/challenge", new { })).StatusCode);
        var rejected = await limited.Client.PostAsJsonAsync(Root + "/gis/challenge", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(0, limited.Keys.Calls);
    }

    [Fact]
    public async Task Key_service_failure_consumes_challenge_fails_closed_and_uses_failure_backoff()
    {
        using var host = new GisHost(); host.Keys.Status = HttpStatusCode.ServiceUnavailable;
        var challenge = await host.Challenge();
        await Error(await host.Complete(challenge), HttpStatusCode.ServiceUnavailable, "google_unavailable");
        await Error(await host.Complete(challenge), HttpStatusCode.Unauthorized, "invalid_google_credentials");
        await Error(await host.Complete(await host.Challenge()), HttpStatusCode.ServiceUnavailable, "google_unavailable");
        Assert.Equal(1, host.Keys.Calls); await host.AssertNoAccounts();
        host.Clock.Advance(TimeSpan.FromSeconds(6)); host.Keys.Status = HttpStatusCode.OK;
        Assert.Equal(HttpStatusCode.OK, (await host.Complete(await host.Challenge())).StatusCode);
        Assert.Equal(2, host.Keys.Calls); Assert.Equal(0, host.Exchange.Calls);
    }

    [Fact]
    public async Task Public_key_cache_honors_expiry_and_unknown_kid_rotation_without_stale_fallback()
    {
        using var signing = new SigningMaterial("key-one"); using var rotated = new SigningMaterial("key-two");
        var clock = new TestClock(); using var service = new KeyHandler(signing) { MaxAge = TimeSpan.FromSeconds(60) };
        using var cache = new CentGooglePublicKeys(new TestHttpFactory(service), clock);
        var validator = new CentGoogleIdTokenValidator(cache); const string nonce = "test-nonce";
        var claims = StandardClaims(nonce);
        var original = Sign(claims, signing.KeyWithId(signing.KeyId));
        var next = Sign(claims, rotated.KeyWithId(rotated.KeyId));
        await validator.Validate(original, "test-web-client", CentModule.Hash(nonce), default);
        await validator.Validate(original, "test-web-client", CentModule.Hash(nonce), default);
        Assert.Equal(1, service.Calls);
        service.Certificates = rotated.Certificates;
        Assert.Equal(401, (await Assert.ThrowsAsync<CentException>(() => validator.Validate(next, "test-web-client", CentModule.Hash(nonce), default))).Status);
        Assert.Equal(1, service.Calls); clock.Advance(TimeSpan.FromSeconds(31));
        await validator.Validate(next, "test-web-client", CentModule.Hash(nonce), default);
        Assert.Equal(2, service.Calls);
        Assert.Equal(401, (await Assert.ThrowsAsync<CentException>(() => validator.Validate(original, "test-web-client", CentModule.Hash(nonce), default))).Status);
        clock.Advance(TimeSpan.FromSeconds(61)); service.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(503, (await Assert.ThrowsAsync<CentException>(() => validator.Validate(next, "test-web-client", CentModule.Hash(nonce), default))).Status);
        Assert.Equal(3, service.Calls);
    }

    [Theory]
    [InlineData("bad-json")]
    [InlineData("empty")]
    [InlineData("invalid-certificate")]
    [InlineData("oversized")]
    [InlineData("timeout")]
    public async Task Malformed_or_unavailable_key_responses_fail_closed(string failure)
    {
        using var host = new GisHost();
        host.Keys.ResponseOverride = failure switch {
            "bad-json" => "{", "empty" => "{}", "invalid-certificate" => "{\"test-key\":\"not-a-certificate\"}",
            "oversized" => new string(' ', 65537), _ => null
        };
        host.Keys.Timeout = failure == "timeout";
        await Error(await host.Complete(await host.Challenge()), HttpStatusCode.ServiceUnavailable, "google_unavailable");
        await host.AssertNoAccounts(); Assert.Equal(0, host.Exchange.Calls);
    }

    [Theory]
    [InlineData("max-age", 31, 1)]
    [InlineData("aged", 31, 2)]
    [InlineData("no-cache", 0, 2)]
    [InlineData("no-store", 0, 2)]
    [InlineData("zero", 0, 2)]
    [InlineData("missing", 61, 2)]
    [InlineData("cap", 21601, 2)]
    public async Task Public_key_cache_obeys_Cache_Control_Age_and_conservative_bounds(string policy, int seconds, int requests)
    {
        using var signing = new SigningMaterial("test-key");
        using var service = new KeyHandler(signing) {
            MaxAge = policy == "cap" ? TimeSpan.FromDays(1) : policy == "zero" ? TimeSpan.Zero : TimeSpan.FromSeconds(60),
            Age = policy == "aged" ? TimeSpan.FromSeconds(40) : TimeSpan.Zero,
            NoCache = policy == "no-cache", NoStore = policy == "no-store", OmitCacheControl = policy == "missing"
        };
        var clock = new TestClock(); using var cache = new CentGooglePublicKeys(new TestHttpFactory(service), clock);
        await cache.Get("test-key", default);
        clock.Advance(TimeSpan.FromSeconds(seconds));
        await cache.Get("test-key", default);
        Assert.Equal(requests, service.Calls);
    }

    [Theory]
    [InlineData("accounts.google.com", false)]
    [InlineData("https://accounts.google.com", true)]
    public async Task Both_Google_issuers_and_optional_authorized_party_are_supported(string issuer, bool azp)
    {
        using var signing = new SigningMaterial("test-key"); using var service = new KeyHandler(signing);
        using var cache = new CentGooglePublicKeys(new TestHttpFactory(service));
        var claims = StandardClaims("nonce"); claims["iss"] = issuer; if (!azp) claims.Remove("azp");
        var identity = await new CentGoogleIdTokenValidator(cache).Validate(Sign(claims, signing.KeyWithId("test-key")),
            "test-web-client", CentModule.Hash("nonce"), default);
        Assert.Equal("google-gis-subject", identity.Subject);
    }

    [Fact]
    public async Task Concurrent_key_requests_share_one_refresh()
    {
        using var signing = new SigningMaterial("test-key"); using var service = new KeyHandler(signing) { Hold = true };
        using var cache = new CentGooglePublicKeys(new TestHttpFactory(service));
        var first = cache.Get("test-key", default);
        await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = cache.Get("test-key", default);
        service.Release.TrySetResult();
        Assert.Same(await first, await second); Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Google_flows_cannot_consume_each_others_attempts_when_mode_changes()
    {
        using var host = new GisHost(); var gis = await host.Challenge();
        var config = host.App.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        config["Cent:Google:Mode"] = "AuthorizationCode";
        config["Cent:Google:ClientSecret"] = "test-placeholder-secret";
        config["Cent:Google:RedirectUri"] = "https://itatest.ir/auth/google/callback";
        var start = (await (await host.Client.PostAsync(Root + "/start", null)).Content.ReadFromJsonAsync<CentGoogleStart>())!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync(Root + "/complete", new {
            state = gis.ChallengeId, codeVerifier = gis.BrowserBinding, code = "placeholder-code"
        })).StatusCode);
        config["Cent:Google:Mode"] = "GisIdToken";
        await Error(await host.Complete(gis with { ChallengeId = start.State, BrowserBinding = start.CodeVerifier }),
            HttpStatusCode.Unauthorized, "invalid_google_credentials");
        Assert.Equal(HttpStatusCode.OK, (await host.Complete(gis)).StatusCode);
        using var scope = host.App.Services.CreateScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<CentDbContext>().GoogleAttempts.SingleAsync();
        Assert.Equal(CentModule.Hash(start.State), attempt.StateHash); Assert.Equal(0, host.Exchange.Calls);
        var discovery = await (await host.Client.GetAsync(Root + "/config")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("GisIdToken", discovery.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Cent_disabled_registers_no_GIS_services_or_routes_even_with_GIS_environment_values()
    {
        using var host = new CentFactory(enabled: false, environmentVariables: new Dictionary<string, string?> {
            ["Cent__Google__Mode"] = "GisIdToken", ["Cent__Google__Enabled"] = "true", ["Cent__Google__ClientId"] = "test-web-client"
        });
        using var client = host.CreateClient();
        Assert.Null(host.Services.GetService<ICentGoogleIdTokenValidator>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Root + "/config")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(Root + "/gis/challenge", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(Root + "/gis/complete", new { })).StatusCode);
    }

    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response) {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    private static Dictionary<string, object> StandardClaims(string nonce) => new() {
        ["iss"] = "https://accounts.google.com", ["aud"] = "test-web-client", ["sub"] = "google-gis-subject",
        ["email"] = "gis@example.com", ["email_verified"] = true, ["name"] = "GIS Student", ["nonce"] = nonce,
        ["iat"] = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds(), ["exp"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
        ["azp"] = "test-web-client"
    };
    private static string Sign(Dictionary<string, object> claims, SecurityKey key, string algorithm = SecurityAlgorithms.RsaSha256) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(new SigningCredentials(key, algorithm)), JwtPayload.Deserialize(JsonSerializer.Serialize(claims))));

    // Generate an intentionally duplicate-claim signed fixture; verification still runs through IdentityModel.
    private static string SignRaw(string payload, SigningMaterial material)
    {
        var header = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "RS256", kid = material.KeyId })));
        var message = header + "." + WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        return message + "." + WebEncoders.Base64UrlEncode(material.Rsa.SignData(Encoding.ASCII.GetBytes(message), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private sealed class GisHost : IDisposable
    {
        private readonly CentFactory factory;
        public SigningMaterial Signing { get; } = new("test-key");
        public KeyHandler Keys { get; }
        public TestClock Clock { get; } = new();
        public ForbiddenExchange Exchange { get; } = new();
        public WebApplicationFactory<Program> App { get; }
        public HttpClient Client { get; }
        public GisHost(bool production = true, string origin = "https://itatest.ir", string backend = "https://lerona-app.com")
        {
            Keys = new(Signing);
            factory = new CentFactory(production: production, googleClient: Exchange, environmentVariables: new Dictionary<string, string?> {
                ["Cent__Google__Enabled"] = "true", ["Cent__Google__Mode"] = "GisIdToken", ["Cent__Google__ClientId"] = "test-web-client",
                ["Cent__Google__ClientSecret"] = "", ["Cent__Google__RedirectUri"] = "", ["Cent__AllowedOrigins__0"] = origin,
                ["Cent__AllowedOrigins__1"] = "https://second.example"
            });
            App = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                services.AddHttpClient(CentGooglePublicKeys.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Keys);
                services.AddSingleton(sp => new CentGooglePublicKeys(sp.GetRequiredService<IHttpClientFactory>(), Clock));
            }));
            Client = App.CreateClient(new() { BaseAddress = new Uri(backend) });
            Client.DefaultRequestHeaders.Add("Origin", origin); Client.DefaultRequestHeaders.Add("X-Cent-GIS", "1");
        }
        public async Task<CentGoogleGisChallenge> Challenge()
        {
            using var response = await Client.PostAsJsonAsync(Root + "/gis/challenge", new { });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<CentGoogleGisChallenge>())!;
        }
        public Dictionary<string, object> Claims(CentGoogleGisChallenge challenge) => StandardClaims(challenge.Nonce);
        public string Token(CentGoogleGisChallenge challenge, Action<Dictionary<string, object>>? change = null,
            SecurityKey? key = null, string algorithm = SecurityAlgorithms.RsaSha256)
        {
            var claims = Claims(challenge); change?.Invoke(claims); return Sign(claims, key ?? Signing.KeyWithId(Signing.KeyId), algorithm);
        }
        public Task<HttpResponseMessage> Complete(CentGoogleGisChallenge challenge, string? token = null) =>
            Client.PostAsJsonAsync(Root + "/gis/complete", new { challenge.ChallengeId, challenge.BrowserBinding, idToken = token ?? Token(challenge) });
        public async Task AssertNoAccounts()
        {
            using var scope = App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
            Assert.Empty(await db.Users.ToListAsync()); Assert.Empty(await db.Sessions.ToListAsync());
        }
        public void Dispose() { Client.Dispose(); App.Dispose(); factory.Dispose(); Signing.Dispose(); }
    }

    private sealed class SigningMaterial : IDisposable
    {
        public string KeyId { get; }
        public RSA Rsa { get; } = RSA.Create(2048);
        public Dictionary<string, string> Certificates { get; }
        public SigningMaterial(string id)
        {
            KeyId = id;
            var request = new CertificateRequest("CN=Cent GIS Test", Rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(2));
            Certificates = new() { [id] = cert.ExportCertificatePem() };
        }
        public RsaSecurityKey KeyWithId(string id) => new(Rsa) { KeyId = id };
        public void Dispose() => Rsa.Dispose();
    }
    private sealed class KeyHandler(SigningMaterial signing) : HttpMessageHandler
    {
        public Dictionary<string, string> Certificates { get; set; } = signing.Certificates;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(1);
        public TimeSpan Age { get; set; }
        public bool NoCache { get; set; }
        public bool NoStore { get; set; }
        public bool OmitCacheControl { get; set; }
        public string? ResponseOverride { get; set; }
        public bool Timeout { get; set; }
        public bool Hold { get; set; }
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal(CentGooglePublicKeys.Endpoint, request.RequestUri!.AbsoluteUri);
            Interlocked.Increment(ref Calls); Entered.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(ct);
            if (Timeout) throw new TaskCanceledException();
            var response = new HttpResponseMessage(Status) { Content = new StringContent(ResponseOverride ?? JsonSerializer.Serialize(Certificates)) };
            if (!OmitCacheControl) response.Headers.CacheControl = new() { MaxAge = MaxAge, NoCache = NoCache, NoStore = NoStore };
            response.Headers.Age = Age; return response;
        }
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
    private sealed class TestHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) { Assert.Equal(CentGooglePublicKeys.HttpClientName, name); return new(handler, false); }
    }
    private sealed class ForbiddenExchange : ICentGoogleClient
    {
        public int Calls;
        public Task<CentGoogleIdentity> ExchangeAndValidate(CentGoogleSettings settings, string code, string verifier, string nonceHash, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); throw new InvalidOperationException("GIS must never exchange an authorization code.");
        }
    }
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly string text;
        public UnknownLengthContent(string text) { this.text = text; Headers.ContentType = new("application/json"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();
        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken ct) => stream.Write(Encoding.UTF8.GetBytes(text));
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(text)));
    }
}
