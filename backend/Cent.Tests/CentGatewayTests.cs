using System.Net;
using System.Text;
using System.Text.Json;
using Cent.Module;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Cent.Tests;

public sealed class CentGatewayTests
{
    [Theory]
    [InlineData("SANDBOX", "sandbox.zarinpal.com")]
    [InlineData("ZARINPAL", "payment.zarinpal.com")]
    public async Task Gateway_uses_order_environment_and_stored_rial_amount(string provider, string host)
    {
        var handler = new RecordingHandler("{\"data\":{\"code\":101,\"ref_id\":123456}}");
        var config = Configuration();
        var gateway = new CentGateway(new HttpClient(handler), config, new ProductionEnvironment());
        Assert.False(gateway.IsMock);
        var order = new CentOrder { Provider = provider, AmountRial = 250000, Authority = "A000000000000000000000000000000000001" };
        var verification = await gateway.Verify(order, default);
        Assert.Equal(101, verification.Code); Assert.Equal("123456", verification.Reference);
        Assert.Equal(host, handler.Uri!.Host);
        Assert.Equal(250000, JsonDocument.Parse(handler.Body!).RootElement.GetProperty("amount").GetInt64());
        Assert.Equal(host, new Uri(gateway.CheckoutUrl(order)).Host);
    }
    [Fact]
    public async Task Request_uses_rials_and_the_configured_callback_not_client_input()
    {
        var handler = new RecordingHandler("{\"data\":{\"code\":100,\"authority\":\"A000000000000000000000000000000000001\"}}");
        var gateway = new CentGateway(new HttpClient(handler), Configuration(), new ProductionEnvironment());
        var order = new CentOrder { Provider = "ZARINPAL", AmountRial = 1500000, Title = "Training" };
        await gateway.Request(order, "student@example.com", default);
        var json = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("IRR", json.GetProperty("currency").GetString());
        Assert.Equal(1500000, json.GetProperty("amount").GetInt64());
        Assert.Equal("https://api.example/api/cent/v1/payments/callback", json.GetProperty("callback_url").GetString());
    }
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["Cent:Payments:MerchantId"] = Guid.NewGuid().ToString(), ["Cent:Payments:AllowDevelopmentMock"] = "true",
        ["Cent:Payments:Sandbox"] = "false", ["Cent:ClientBaseUrl"] = "https://cent.example/cent-s", ["Cent:PublicApiBaseUrl"] = "https://api.example"
    }).Build();
    private sealed class RecordingHandler(string payload) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri; Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Cent.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
