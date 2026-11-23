using System.Net;
using System.Net.Http.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cent.Exam.Tests;

public sealed class PreviewRateLimitTests
{
    [Fact]
    public async Task Exhausted_preview_policy_returns_429_JSON_and_retry_delay_instead_of_empty_503()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("probe", policy => {
            policy.PermitLimit = 1; policy.Window = TimeSpan.FromMinutes(1); policy.QueueLimit = 0;
        }));
        builder.Services.Configure<RateLimiterOptions>(PreviewRateLimits.Configure);
        await using var app = builder.Build();
        app.UseRouting(); app.UseRateLimiter();
        app.MapGet("/api/cent/v1/probe", () => "ok").RequireRateLimiting("probe");
        await app.StartAsync();
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/cent/v1/probe")).StatusCode);
        var rejected = await client.GetAsync("/api/cent/v1/probe");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.RetryAfter!.Delta > TimeSpan.Zero);
        Assert.True(rejected.Headers.CacheControl!.NoStore);
        var error = await rejected.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("rate_limited", error!["code"]);
    }
}
