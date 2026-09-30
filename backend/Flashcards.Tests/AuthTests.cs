using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Flashcards.Tests;

public sealed class AuthTests : IAsyncLifetime
{
    private const string Password = "My long private passphrase 2026!";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private AuthApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connection.CreateFunction("ISJSON", (string value) =>
        {
            try { using var document = JsonDocument.Parse(value); return 1; }
            catch (JsonException) { return 0; }
        });
        await _connection.OpenAsync();
        _factory = new AuthApiFactory(_connection);
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Registration_saves_only_a_password_hash_and_prevents_duplicate_email()
    {
        var tokens = await RegisterAsync("One@Example.com");
        Assert.NotEmpty(tokens.AccessToken);
        Assert.NotEmpty(tokens.RefreshToken);

        var duplicate = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email = "ONE@example.com", password = Password, displayName = "Duplicate" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var user = await database.Users.SingleAsync();
        Assert.Equal("ONE@EXAMPLE.COM", user.NormalizedEmail);
        Assert.NotEqual(Password, user.PasswordHash);
        Assert.DoesNotContain(Password, user.PasswordHash!);
        Assert.DoesNotContain(tokens.RefreshToken, (await database.RefreshTokens.SingleAsync()).TokenHash);
        var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<User>>();
        Assert.NotEqual(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(user, user.PasswordHash!, Password));
        Assert.Null(typeof(UserChange).GetProperty(nameof(User.PasswordHash)));
    }

    [Fact]
    public async Task Login_and_access_token_validation_reject_bad_credentials_and_tampering()
    {
        await RegisterAsync("one@example.com");
        var wrong = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "one@example.com", password = "incorrect" });
        var unknown = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "missing@example.com", password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "ONE@EXAMPLE.COM", password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<AuthTokens>())!;
        Assert.Equal("no-store", login.Headers.CacheControl?.ToString());
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var account = await _client.GetAsync("/api/v1/me/");
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        var accountJson = await account.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("one@example.com", accountJson.GetProperty("email").GetString());
        Assert.False(accountJson.TryGetProperty("passwordHash", out _));

        var dot = tokens.AccessToken.LastIndexOf('.');
        var signature = tokens.AccessToken[(dot + 1)..];
        var invalidSignature = signature[0] == 'a' ? "b" + signature[1..] : "a" + signature[1..];
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken[..(dot + 1)] + invalidSignature);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/me/")).StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_token_and_reuse_revokes_the_whole_session()
    {
        var original = await RegisterAsync("one@example.com");
        var refreshed = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = original.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var rotated = (await refreshed.Content.ReadFromJsonAsync<AuthTokens>())!;
        Assert.NotEqual(original.RefreshToken, rotated.RefreshToken);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/me/")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = original.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = rotated.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_refresh_and_access_tokens_for_the_session()
    {
        var tokens = await RegisterAsync("one@example.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.NotImplemented,
            (await _client.PostAsJsonAsync("/api/v1/decks", new { name = "Example" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task Token_validation_checks_issuer_expiry_and_user_session_pair()
    {
        var tokens = await RegisterAsync("first@example.com");
        var original = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken);
        var sessionId = original.Claims.Single(claim => claim.Type == "sid").Value;
        var userId = original.Claims.Single(claim => claim.Type == JwtRegisteredClaimNames.Sub).Value;
        var key = new SymmetricSecurityKey(Convert.FromBase64String(TestApiFactory.TestSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var invalid = new[]
        {
            new JwtSecurityToken("wrong-issuer", "Flashcards.Mobile", [new Claim("sub", userId), new Claim("sid", sessionId)],
                expires: DateTime.UtcNow.AddMinutes(2), signingCredentials: credentials),
            new JwtSecurityToken("Flashcards.Api", "Flashcards.Mobile", [new Claim("sub", userId), new Claim("sid", sessionId)],
                notBefore: DateTime.UtcNow.AddMinutes(-10), expires: DateTime.UtcNow.AddMinutes(-2), signingCredentials: credentials),
            new JwtSecurityToken("Flashcards.Api", "Flashcards.Mobile", [new Claim("sub", Guid.NewGuid().ToString()), new Claim("sid", sessionId)],
                expires: DateTime.UtcNow.AddMinutes(2), signingCredentials: credentials)
        };
        foreach (var candidate in invalid)
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(candidate));
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        }
    }

    [Fact]
    public async Task User_owned_data_is_isolated_and_never_uses_client_user_ids()
    {
        var first = await RegisterAsync("first@example.com");
        var second = await RegisterAsync("second@example.com");
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var firstUser = await database.Users.SingleAsync(user => user.Email == "first@example.com");
        var deck = new Deck { UserId = firstUser.Id, Name = "Private" };
        var card = new Card { Deck = deck, FrontText = "Private front", Meaning = "Private meaning" };
        database.AddRange(deck, card, new CardReviewState { Card = card, DueDate = DateOnly.FromDateTime(DateTime.UtcNow) },
            new UserSettings { UserId = firstUser.Id, Theme = "dark" });
        await database.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.AccessToken);
        Assert.Equal("Private", (await (await _client.GetAsync("/api/v1/me/decks")).Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/me/cards/{card.Id}/review-state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/me/settings")).StatusCode);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.AccessToken);
        Assert.Empty((await (await _client.GetAsync("/api/v1/me/decks")).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/me/cards/{card.Id}/review-state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/v1/me/settings")).StatusCode);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/me/decks")).StatusCode);
    }

    [Fact]
    public async Task Auth_endpoints_limit_abusive_requests()
    {
        for (var attempt = 0; attempt < 10; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "none@example.com", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "none@example.com", password = Password })).StatusCode);
    }

    private async Task<AuthTokens> RegisterAsync(string email)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, displayName = "Student" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
    }

    private sealed class AuthApiFactory(SqliteConnection connection) : TestApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<FlashcardsDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<FlashcardsDbContext>>();
                services.AddDbContext<FlashcardsDbContext>(options => options.UseSqlite(connection));
            });
        }
    }
}
