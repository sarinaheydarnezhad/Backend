using System.Net;
using Microsoft.AspNetCore.Hosting;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed class ConcurrencyLimitTests
{
    [Fact]
    public async Task Saturated_instance_rejects_without_queuing_and_recovers_when_the_permit_is_released()
    {
        await using var factory = new LimitedApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "https://mobile.example.test");
        var limiter = factory.Services.GetRequiredService<ConcurrencyLimiter>();
        using (var permit = await limiter.AcquireAsync(1))
        {
            Assert.True(permit.IsAcquired);
            var response = await client.GetAsync("/api/v1/plans");
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter!.Delta);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Contains("Retry-After", response.Headers.GetValues("Access-Control-Expose-Headers"));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/plans")).StatusCode);
    }

    private sealed class LimitedApiFactory : TestApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RateLimits:ConcurrentRequests", "1");
            builder.UseSetting("Cors:AllowedOrigins:0", "https://mobile.example.test");
        }
    }
}
