using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Application.Abstractions;
using Flashcards.Infrastructure;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flashcards.Tests;

public sealed class FoundationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public FoundationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Api_starts_and_serves_a_versioned_status_dto()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Flashcards API", body.GetProperty("name").GetString());
        Assert.Equal("v1", body.GetProperty("apiVersion").GetString());
    }

    [Fact]
    public async Task Health_endpoint_returns_healthy_without_a_database()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Anonymous_deck_requests_are_rejected(string name)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/decks", new { name });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Valid_deck_request_requires_authentication()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/decks", new { name = "Sample" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unhandled_errors_return_safe_problem_details()
    {
        using var factory = _factory.WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Production");
            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<IServiceMetadata>();
                services.AddSingleton<IServiceMetadata, ThrowingServiceMetadata>();
            });
        });
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/status");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500, body.GetProperty("status").GetInt32());
        Assert.True(body.TryGetProperty("traceId", out _));
        Assert.DoesNotContain("sensitive test marker", body.ToString());
    }

    [Fact]
    public void Application_dependency_is_resolvable()
    {
        _factory.CreateClient().Dispose();
        var metadata = _factory.Services.GetRequiredService<IServiceMetadata>();

        Assert.Equal("v1", metadata.Get().ApiVersion);
    }

    [Fact]
    public void Infrastructure_registers_sql_server_only_when_configured()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FlashcardsDb"] = "Server=localhost;Database=Flashcards;Trusted_Connection=True;TrustServerCertificate=True",
            ["Auth:Issuer"] = "Flashcards.Api",
            ["Auth:Audience"] = "Flashcards.Mobile",
            ["Auth:SigningKey"] = TestApiFactory.TestSigningKey
        }).Build();
        var services = new ServiceCollection().AddInfrastructure(config);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.True(scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Database.IsSqlServer());
    }

    [Fact]
    public async Task Openapi_is_available_only_in_development()
    {
        using var development = _factory.WithWebHostBuilder(host => host.UseEnvironment("Development"));
        using var production = _factory.WithWebHostBuilder(host => host.UseEnvironment("Production"));
        using var developmentClient = development.CreateClient();
        using var productionClient = production.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await developmentClient.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await productionClient.GetAsync("/openapi/v1.json")).StatusCode);
    }

    private sealed class ThrowingServiceMetadata : IServiceMetadata
    {
        public ServiceMetadata Get() => throw new InvalidOperationException("sensitive test marker");
    }
}

public class TestApiFactory : WebApplicationFactory<Program>
{
    public static string TestSigningKey { get; } = Convert.ToBase64String(Enumerable.Repeat((byte)0x5a, 32).ToArray());

    public TestApiFactory() => Environment.SetEnvironmentVariable("Auth__SigningKey", TestSigningKey);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Issuer"] = "Flashcards.Api",
            ["Auth:Audience"] = "Flashcards.Mobile",
            ["Auth:SigningKey"] = TestSigningKey
        }));
    }
}
