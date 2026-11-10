using System.Net;
using Flashcards.Infrastructure;
using Flashcards.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed class KavenegarOtpSenderTests
{
    private const string Accepted = """{"return":{"status":200},"entries":[{"messageid":123,"status":1}]}""";

    [Fact]
    public void Environment_variables_select_the_actual_backend_adapter_and_placeholders_fail_closed()
    {
        var prefix = "LERONA_ENV_TEST_" + Guid.NewGuid().ToString("N") + "_";
        var names = new[] { "Sms__Provider", "Kavenegar__ApiKey", "Kavenegar__OtpTemplate" };
        try
        {
            Environment.SetEnvironmentVariable(prefix + names[0], "Kavenegar");
            Environment.SetEnvironmentVariable(prefix + names[1], "PASTE_MY_KAVENEGAR_API_KEY_HERE");
            Environment.SetEnvironmentVariable(prefix + names[2], "YOUR_APPROVED_TEMPLATE_NAME");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Issuer"] = "test", ["Auth:Audience"] = "test",
                ["Auth:SigningKey"] = Convert.ToBase64String(new byte[32])
            }).AddEnvironmentVariables(prefix).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddInfrastructure(configuration);
            using var container = services.BuildServiceProvider();
            Assert.Equal("Kavenegar", configuration["Sms:Provider"]);
            Assert.False(Assert.IsType<KavenegarOtpSender>(container.GetRequiredService<ISmsOtpSender>()).IsConfigured);
        }
        finally { foreach (var name in names) Environment.SetEnvironmentVariable(prefix + name, null); }
    }

    [Fact]
    public async Task Sends_official_lookup_form_and_preserves_leading_zero_in_code()
    {
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.kavenegar.com/v1/test-key/verify/lookup.json", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            var form = await request.Content.ReadAsStringAsync();
            Assert.Equal("receptor=09121234567&token=001234&template=lerona-login&type=sms", form);
            return Response(Accepted);
        });
        using var client = new HttpClient(handler);
        await new KavenegarOtpSender(client, Configuration()).SendAsync("+989121234567", "001234", default);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(null, "login")]
    [InlineData("", "login")]
    [InlineData("test-key", null)]
    [InlineData("bad/key?", "login")]
    [InlineData("bad\nkey", "login")]
    [InlineData("PASTE_MY_KAVENEGAR_API_KEY_HERE", "login")]
    [InlineData("test-key", "YOUR_APPROVED_TEMPLATE_NAME")]
    public async Task Missing_configuration_fails_closed_without_network(string? key, string? template)
    {
        var handler = new Handler(_ => Task.FromResult(Response(Accepted)));
        using var client = new HttpClient(handler);
        var sender = new KavenegarOtpSender(client, Configuration(key, template));
        Assert.False(sender.IsConfigured);
        var error = await Assert.ThrowsAsync<PhoneAuthException>(() => sender.SendAsync("+989121234567", "123456", default));
        Assert.Equal("sms_unavailable", error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("invalid-json")]
    [InlineData("{\"return\":{\"status\":424,\"message\":\"secret\"},\"entries\":null}")]
    [InlineData("{\"return\":{\"status\":200},\"entries\":[]}")]
    [InlineData("{\"return\":{\"status\":200},\"entries\":[{\"messageid\":0}]}")]
    [InlineData("{\"return\":{\"status\":200},\"entries\":[{\"messageid\":123,\"status\":11}]}")]
    [InlineData("{\"return\":{\"status\":200},\"entries\":[{\"messageid\":123,\"status\":14}]}")]
    [InlineData("{\"return\":{\"status\":200},\"entries\":[{\"messageid\":123}]}")]
    [InlineData("{\"return\":{\"status\":\"200\"},\"entries\":[{\"messageid\":123}]}")]
    public async Task Rejected_or_malformed_response_never_reports_success(string body)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(Response(body))));
        var error = await Assert.ThrowsAsync<PhoneAuthException>(() =>
            new KavenegarOtpSender(client, Configuration()).SendAsync("+989121234567", "123456", default));
        Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("test-key", error.ToString());
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task Http_failures_map_to_safe_unavailable(int status)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        var error = await Assert.ThrowsAsync<PhoneAuthException>(() =>
            new KavenegarOtpSender(client, Configuration()).SendAsync("+989121234567", "123456", default));
        Assert.Equal("sms_unavailable", error.Code);
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("")]
    [InlineData("unknown")]
    public async Task Provider_debug_or_unknown_mode_cannot_send_an_authentication_sms(string debugMode)
    {
        var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/account/config.json", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Response("{\"return\":{\"status\":200},\"entries\":{\"debugmode\":\"" + debugMode + "\"}}"));
        }, autoLiveCheck: false);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<PhoneAuthException>(() => new KavenegarOtpSender(client, Configuration())
            .SendAsync("+989121234567", "123456", default));
        Assert.Equal("sms_unavailable", error.Code);
        Assert.Equal(1, handler.Calls); // Only the read-only check; no lookup/send call.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_and_timeout_errors_do_not_leak_provider_details(bool timeout)
    {
        using var client = new HttpClient(new Handler(_ => throw (timeout
            ? new TaskCanceledException("private-url/test-key") : new HttpRequestException("private-url/test-key"))));
        var error = await Assert.ThrowsAsync<PhoneAuthException>(() =>
            new KavenegarOtpSender(client, Configuration()).SendAsync("+989121234567", "123456", default));
        Assert.DoesNotContain("test-key", error.ToString());
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(new Handler(_ => Task.FromResult(Response(Accepted))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new KavenegarOtpSender(client, Configuration()).SendAsync("+989121234567", "123456", cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_does_not_propagate_sensitive_transport_messages(bool duringPreflight)
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(_ =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException("private-provider-url/test-key");
        }, autoLiveCheck: !duringPreflight));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new KavenegarOtpSender(client, Configuration()).SendAsync("+989121234567", "123456", cancellation.Token));
        Assert.DoesNotContain("test-key", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    [InlineData("Mock")]
    public void Simulated_providers_cannot_be_enabled_in_runtime_configuration(string provider)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Issuer"] = "test", ["Auth:Audience"] = "test", ["Auth:SigningKey"] = Convert.ToBase64String(new byte[32]),
            ["Sms:Provider"] = provider
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddInfrastructure(config));
    }

    private static IConfiguration Configuration(string? key = "test-key", string? template = "lerona-login") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kavenegar:ApiKey"] = key, ["Kavenegar:OtpTemplate"] = template
        }).Build();
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, bool autoLiveCheck = true) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (autoLiveCheck && request.RequestUri!.AbsolutePath.EndsWith("/account/config.json"))
                return Task.FromResult(Response("""{"return":{"status":200},"entries":{"debugmode":"disabled"}}"""));
            return respond(request);
        }
    }
}
