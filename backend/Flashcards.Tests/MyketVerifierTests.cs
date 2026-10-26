using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Infrastructure.Subscriptions;

namespace Flashcards.Tests;

public sealed class MyketVerifierTests
{
    [Fact]
    public async Task Uses_official_post_verify_contract_and_server_only_access_header()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://developer.myket.ir/api/partners/applications/com.example.lerona/purchases/products/plus1/verify", request.RequestUri!.AbsoluteUri);
            Assert.Equal("server-secret", request.Headers.GetValues("X-Access-Token").Single());
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("purchase-token", body.RootElement.GetProperty("tokenId").GetString());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new {
                purchaseTime = 1793016000000L, developerPayload = "server-payload", purchaseState = 0, consumptionState = 0 }) };
        });
        using var client = new HttpClient(handler);
        var verifier = new MyketVerifier(client, new() { PackageName = "com.example.lerona", AccessToken = "server-secret" });
        var receipt = await verifier.VerifyAsync("plus1", "purchase-token", default);
        Assert.Equal("server-payload", receipt.DeveloperPayload);
        Assert.Equal(1793016000000L, receipt.PurchaseTime);
    }

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(403, "{}")]
    [InlineData(500, "{}")]
    [InlineData(200, "not json")]
    [InlineData(200, "{}")]
    [InlineData(200, "{\"purchaseTime\":1,\"developerPayload\":\"x\",\"purchaseState\":\"0\",\"consumptionState\":0}")]
    public async Task Unsuccessful_or_malformed_provider_responses_fail_closed(int status, string body)
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) }));
        using var client = new HttpClient(handler);
        var verifier = new MyketVerifier(client, new() { PackageName = "com.example.lerona", AccessToken = "secret" });
        var error = await Assert.ThrowsAsync<PaymentException>(() => verifier.VerifyAsync("plus1", "token", default));
        Assert.Equal(503, error.StatusCode);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
