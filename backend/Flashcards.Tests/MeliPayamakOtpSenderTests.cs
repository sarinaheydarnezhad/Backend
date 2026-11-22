using System.Net;
using System.Text;
using System.Text.Json;
using Flashcards.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Flashcards.Tests;

public sealed class MeliPayamakOtpSenderTests
{
    [Theory]
    [InlineData("register", 101)]
    [InlineData("reset", 202)]
    public async Task UsesThePurposeSpecificPatternAndPreservesLeadingZeros(string purpose, int bodyId)
    {
        using var client = new HttpClient(new Handler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://console.melipayamak.com/api/send/shared/test-key", request.RequestUri?.AbsoluteUri);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(bodyId, json.RootElement.GetProperty("bodyId").GetInt32());
            Assert.Equal("09121234567", json.RootElement.GetProperty("to").GetString());
            Assert.Equal("001234", json.RootElement.GetProperty("args")[0].GetString());
            return Reply("""{"recId":"3741437414"}""");
        }));
        ISmsOtpSender sender = new MeliPayamakOtpSender(client, Configuration());
        Assert.True(sender.IsConfigured);
        await sender.SendAsync("+989121234567", "001234", purpose, default);
    }

    [Theory]
    [InlineData("""{"status":"Rejected"}""")]
    [InlineData("""{"recId":"0"}""")]
    [InlineData("invalid JSON")]
    public async Task RejectsResponsesWithoutAPositiveReceipt(string body)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(body))));
        var sender = new MeliPayamakOtpSender(client, Configuration());
        await Assert.ThrowsAsync<PhoneAuthException>(() =>
            sender.SendAsync("+989121234567", "001234", "register", default));
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["MeliPayamak:ApiKey"] = "test-key",
            ["MeliPayamak:RegistrationBodyId"] = "101",
            ["MeliPayamak:ResetBodyId"] = "202"
        }).Build();

    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
