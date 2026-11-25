using System.Net;
using System.Text.Json;
using Cent.Module;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cent.Tests;

public sealed class CentMelipayamakTests
{
    [Theory]
    [InlineData("{\"recId\":12345}", 200, false)]
    [InlineData("{\"recId\":\"12345\"}", 200, false)]
    [InlineData("{\"recId\":-1,\"status\":\"auth error\"}", 200, true)]
    [InlineData("{\"recId\":-2}", 200, true)]
    [InlineData("{\"recId\":0}", 200, true)]
    [InlineData("{\"status\":\"invalid template\"}", 200, true)]
    [InlineData("not json", 200, true)]
    [InlineData("[]", 200, true)]
    [InlineData("null", 200, true)]
    [InlineData("{}", 401, true)]
    [InlineData("{}", 500, true)]
    [InlineData("timeout", 200, true)]
    [InlineData("network", 200, true)]
    public async Task Shared_template_contract_and_provider_failures_are_safe(string response, int status, bool failure)
    {
        using var handler = new Handler(response, status); using var http = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Cent:Sms:Melipayamak:ApiKey"] = "synthetic-test-provider-key", ["Cent:Sms:Melipayamak:BodyId"] = "123"
        }).Build();
        var sender = new CentMelipayamak(http, config, NullLogger<CentMelipayamak>.Instance);
        if (failure) {
            var error = await Assert.ThrowsAsync<CentException>(() => sender.Send("+989123456789", "654321", CentSmsPurpose.SignIn, default));
            Assert.Equal(503, error.Status); Assert.Equal("sms_unavailable", error.Code);
            Assert.DoesNotContain("synthetic", error.Message); Assert.DoesNotContain("654321", error.Message);
        } else await sender.Send("+989123456789", "654321", CentSmsPurpose.SignIn, default);
        Assert.Equal(1, handler.Calls); // Ambiguous delivery is never retried.
    }
    [Fact]
    public async Task Recovery_uses_its_own_template()
    {
        using var handler = new Handler("{\"recId\":12345}", 200, expectedBodyId: 596857);
        using var http = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Cent:Sms:Melipayamak:ApiKey"] = "synthetic-test-provider-key",
            ["Cent:Sms:Melipayamak:BodyId"] = "596855",
            ["Cent:Sms:Melipayamak:RecoveryBodyId"] = "596857",
        }).Build();
        await new CentMelipayamak(http, config, NullLogger<CentMelipayamak>.Instance)
            .Send("+989123456789", "654321", CentSmsPurpose.PasswordReset, default);
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task Lerona_configuration_is_never_used_as_fallback()
    {
        using var handler = new Handler("{}", 200); using var http = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["MeliPayamak:ApiKey"] = "synthetic-unused-key", ["MeliPayamak:RegistrationBodyId"] = "123"
        }).Build();
        await Assert.ThrowsAsync<CentException>(() => new CentMelipayamak(http, config, NullLogger<CentMelipayamak>.Instance).Send("+989123456789", "654321", CentSmsPurpose.SignIn, default));
        Assert.Equal(0, handler.Calls);
    }
    private sealed class Handler(string body, int status, int expectedBodyId = 123) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.Equal(HttpMethod.Post, request.Method);
            Assert.StartsWith("https://console.melipayamak.com/api/send/shared/", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(expectedBodyId, json.RootElement.GetProperty("bodyId").GetInt32());
            Assert.Equal("09123456789", json.RootElement.GetProperty("to").GetString());
            Assert.Single(json.RootElement.GetProperty("args").EnumerateArray());
            if (body == "timeout") throw new TaskCanceledException();
            if (body == "network") throw new HttpRequestException();
            return new((HttpStatusCode)status) { Content = new StringContent(body) };
        }
    }
}
