using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Flashcards.Tests;

public sealed class GoogleAuthTests : IAsyncLifetime
{
    private const string ClientId = "test-web.apps.googleusercontent.com";
    private readonly RSA _key = RSA.Create(2048);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestMetadata _metadata = new();
    private GoogleApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connection.CreateFunction("ISJSON", (string value) =>
        {
            try { using var document = JsonDocument.Parse(value); return 1; }
            catch (JsonException) { return 0; }
        });
        await _connection.OpenAsync();
        _metadata.Configuration.SigningKeys.Add(new RsaSecurityKey(_key.ExportParameters(false)) { KeyId = "google-test-key" });
        _factory = new GoogleApiFactory(_connection, _metadata);
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _connection.DisposeAsync();
        _key.Dispose();
    }

    [Fact]
    public async Task Google_registers_without_phone_password_or_otp_and_returns_existing_app_sessions()
    {
        var response = await SignInAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var tokens = (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/");
        Assert.Equal("student@gmail.com", me.GetProperty("email").GetString());
        Assert.Equal("Google Student", me.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("phoneNumber").ValueKind);
        Assert.False(me.TryGetProperty("googleSubject", out _));
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var user = await database.Users.SingleAsync();
        Assert.Equal("google-subject", user.GoogleSubject);
        Assert.Null(user.PasswordHash);
        Assert.Empty(await database.OtpChallenges.ToListAsync());
        Assert.Single(await database.SyncHeads.ToListAsync());
        Assert.Single(await database.AuthSessions.ToListAsync());
        Assert.NotEqual(tokens.RefreshToken, (await database.RefreshTokens.SingleAsync()).TokenHash);
    }

    [Theory]
    [InlineData("https://accounts.google.com")]
    [InlineData("accounts.google.com")]
    public async Task Repeat_Google_login_uses_subject_without_duplicating_users_or_overwriting_profile(string issuer)
    {
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync()).StatusCode);
        var response = await SignInAsync(Token(issuer: issuer, email: "changed@gmail.com"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal("student@gmail.com", (await database.Users.SingleAsync()).Email);
        Assert.Single(await database.SyncHeads.ToListAsync());
        Assert.Equal(2, await database.AuthSessions.CountAsync());
    }

    [Theory]
    [InlineData("wrong-audience")]
    [InlineData("wrong-issuer")]
    [InlineData("expired")]
    [InlineData("bad-signature")]
    [InlineData("unsigned")]
    [InlineData("missing-subject")]
    [InlineData("unverified-email")]
    [InlineData("invalid-email")]
    [InlineData("malformed")]
    public async Task Invalid_Google_tokens_do_not_create_users_or_sessions(string scenario)
    {
        using var otherKey = RSA.Create(2048);
        var token = scenario switch
        {
            "wrong-audience" => Token(audience: "other-app.apps.googleusercontent.com"),
            "wrong-issuer" => Token(issuer: "https://attacker.example"),
            "expired" => Token(expired: true),
            "bad-signature" => Token(signingKey: otherKey),
            "unsigned" => Token(unsigned: true),
            "missing-subject" => Token(subject: ""),
            "unverified-email" => Token(verified: false),
            "invalid-email" => Token(email: "not-an-email"),
            _ => "not-a-jwt"
        };
        var response = await SignInAsync(token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_google_token", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Empty(await database.Users.ToListAsync());
        Assert.Empty(await database.AuthSessions.ToListAsync());
    }

    [Fact]
    public async Task Matching_email_never_implicitly_links_or_takes_over_an_existing_password_account()
    {
        var registered = await _client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "Student@Gmail.com", password = "My private password 2026!", displayName = "Existing student"
        });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var response = await SignInAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("google_account_conflict", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var user = await database.Users.SingleAsync();
        Assert.Null(user.GoogleSubject);
        Assert.NotNull(user.PasswordHash);
        Assert.Equal("Existing student", user.DisplayName);
        Assert.Single(await database.AuthSessions.ToListAsync());
    }

    [Fact]
    public async Task Archived_Google_accounts_cannot_sign_in_or_be_recreated()
    {
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync()).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            (await database.Users.SingleAsync()).ArchivedAtUtc = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync()).StatusCode);
    }

    [Fact]
    public async Task Google_sessions_use_existing_refresh_rotation_and_logout_revocation()
    {
        var tokens = (await (await SignInAsync()).Content.ReadFromJsonAsync<AuthTokens>())!;
        var refreshed = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var rotated = (await refreshed.Content.ReadFromJsonAsync<AuthTokens>())!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { rotated.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task Unavailable_Google_metadata_fails_closed_without_creating_a_user()
    {
        _metadata.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SignInAsync()).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Users.ToListAsync());
    }

    [Fact]
    public async Task Missing_client_id_disables_Google_instead_of_accepting_any_audience()
    {
        var verifier = new GoogleTokenVerifier(new ConfigurationBuilder().Build(), _metadata);
        var exception = await Assert.ThrowsAsync<GoogleAuthException>(() => verifier.VerifyAsync(Token()));
        Assert.Equal(503, exception.StatusCode);
        Assert.Equal("google_unavailable", exception.Code);
    }

    [Fact]
    public async Task Missing_signing_key_requests_a_metadata_refresh_and_rejects_the_token()
    {
        _metadata.Configuration.SigningKeys.Clear();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync()).StatusCode);
        Assert.True(_metadata.RefreshRequested);
    }

    [Fact]
    public async Task Google_endpoint_uses_existing_authentication_rate_limit()
    {
        for (var attempt = 0; attempt < 10; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync("not-a-jwt")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SignInAsync("not-a-jwt")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Google_endpoint_rejects_missing_id_tokens(string? idToken)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/auth/google", new { idToken })).StatusCode);
    }

    private Task<HttpResponseMessage> SignInAsync(string? token = null) =>
        _client.PostAsJsonAsync("/api/v1/auth/google", new { idToken = token ?? Token() });

    private string Token(string subject = "google-subject", string email = "student@gmail.com", bool verified = true,
        string issuer = "https://accounts.google.com", string audience = ClientId, bool expired = false,
        RSA? signingKey = null, bool unsigned = false)
    {
        var now = DateTime.UtcNow;
        var key = new RsaSecurityKey(signingKey ?? _key) { KeyId = "google-test-key" };
        var jwt = new JwtSecurityToken(issuer, audience,
            [new Claim("sub", subject), new Claim("email", email), new Claim("email_verified", verified ? "true" : "false", ClaimValueTypes.Boolean),
                new Claim("name", "Google Student")],
            notBefore: now.AddMinutes(-10), expires: expired ? now.AddMinutes(-5) : now.AddMinutes(5),
            signingCredentials: unsigned ? null : new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private sealed class TestMetadata : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public OpenIdConnectConfiguration Configuration { get; } = new();
        public bool Fail { get; set; }
        public bool RefreshRequested { get; private set; }
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken) =>
            Fail ? throw new HttpRequestException("Test discovery failure") : Task.FromResult(Configuration);
        public void RequestRefresh() => RefreshRequested = true;
    }

    private sealed class GoogleApiFactory(SqliteConnection connection, TestMetadata metadata) : TestApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Google:WebClientId"] = ClientId }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<FlashcardsDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<FlashcardsDbContext>>();
                services.AddDbContext<FlashcardsDbContext>(options => options.UseSqlite(connection));
                services.RemoveAll<IConfigurationManager<OpenIdConnectConfiguration>>();
                services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(metadata);
            });
        }
    }
}
