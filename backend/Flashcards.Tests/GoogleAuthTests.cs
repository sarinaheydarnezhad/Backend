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

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("invalid-token")]
    public async Task Google_is_disabled_independent_of_token_or_configuration(string? idToken)
    {
        var response=await _client.PostAsJsonAsync("/api/v1/auth/google",new {idToken});
        Assert.Equal(HttpStatusCode.Gone,response.StatusCode);
        using var scope=_factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Empty(await db.Users.ToListAsync());Assert.Empty(await db.AuthSessions.ToListAsync());
    }
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
