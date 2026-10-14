using System.Net;
using System.Text;
using System.Text.Json;
using Flashcards.Infrastructure;
using Flashcards.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed class SmsIrOtpSenderTests
{
    private const string Phone = "+989191234904";
    private const string ApiKey = "test-only-api-key";
    private const string Accepted = "{\"status\":1,\"message\":\"accepted\",\"data\":{\"messageId\":89545112,\"cost\":1}}";

    [Fact]
    public async Task Sends_the_verify_contract_with_the_existing_six_digit_code()
    {
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.sms.ir/v1/send/verify", request.RequestUri!.AbsoluteUri);
            Assert.Equal("text/plain", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal("9191234904", body.RootElement.GetProperty("mobile").GetString());
            Assert.Equal(123456, body.RootElement.GetProperty("templateId").GetInt32());
            var parameter = Assert.Single(body.RootElement.GetProperty("parameters").EnumerateArray());
            Assert.Equal("Code", parameter.GetProperty("name").GetString());
            Assert.Equal("001234", parameter.GetProperty("value").GetString());
            Assert.False(body.RootElement.TryGetProperty("phoneNumber", out _));
            return Response(HttpStatusCode.OK, Accepted);
        });
        using var client = new HttpClient(handler);
        var sender = new SmsIrOtpSender(client, Configuration());
        Assert.True(sender.IsConfigured);
        await sender.SendAsync(Phone, "001234", CancellationToken.None);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(null, "123456")]
    [InlineData("", "123456")]
    [InlineData("bad\r\nkey", "123456")]
    [InlineData(ApiKey, null)]
    [InlineData(ApiKey, "0")]
    [InlineData(ApiKey, "-1")]
    [InlineData(ApiKey, "not-a-template")]
    public async Task Missing_or_invalid_configuration_fails_without_a_request(string? apiKey, string? templateId)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Accepted)));
        using var client = new HttpClient(handler);
        var sender = new SmsIrOtpSender(client, Configuration(apiKey, templateId));
        Assert.False(sender.IsConfigured);
        var error = await Assert.ThrowsAsync<PhoneAuthException>(() => sender.SendAsync(Phone, "123456", CancellationToken.None));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal("sms_unavailable", error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("+14155552671")]
    [InlineData("+982112345678")]
    [InlineData("+989123")]
    [InlineData("09191234904")]
    public async Task Unsupported_recipients_are_not_sent_to_the_provider(string phone)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Accepted)));
        using var client = new HttpClient(handler);
        var sender = new SmsIrOtpSender(client, Configuration());
        var error = await Assert.ThrowsAsync<PhoneAuthException>(() => sender.SendAsync(phone, "123456", CancellationToken.None));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid_phone", error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, Accepted)]
    [InlineData(HttpStatusCode.TooManyRequests, Accepted)]
    [InlineData(HttpStatusCode.InternalServerError, Accepted)]
    [InlineData(HttpStatusCode.OK, "{\"status\":0,\"message\":\"private provider error\",\"data\":null}")]
    [InlineData(HttpStatusCode.OK, "{\"status\":1,\"data\":{\"messageId\":0}}")]
    [InlineData(HttpStatusCode.OK, "{\"status\":1,\"data\":{}}")]
    [InlineData(HttpStatusCode.OK, "{\"status\":\"1\",\"data\":{\"messageId\":123}}")]
    [InlineData(HttpStatusCode.OK, "null")]
    [InlineData(HttpStatusCode.OK, "not-json")]
    public async Task Http_or_provider_failure_never_reports_acceptance(HttpStatusCode status, string body)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(status, body)));
        using var client = new HttpClient(handler);
        var sender = new SmsIrOtpSender(client, Configuration());
        var error = await Assert.ThrowsAsync<PhoneAuthException>(() => sender.SendAsync(Phone, "123456", CancellationToken.None));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal("sms_unavailable", error.Code);
        Assert.DoesNotContain("private provider error", error.Message);
        Assert.DoesNotContain(ApiKey, error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Network_failure_and_timeout_return_safe_errors(bool timeout)
    {
        var handler = new RecordingHandler((_, _) => timeout
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("private timeout"))
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("private network error")));
        using var client = new HttpClient(handler);
        var sender = new SmsIrOtpSender(client, Configuration());
        var error = await Assert.ThrowsAsync<PhoneAuthException>(() => sender.SendAsync(Phone, "123456", CancellationToken.None));
        Assert.Equal("sms_unavailable", error.Code);
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_into_provider_success_or_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Accepted)));
        using var client = new HttpClient(handler);
        var sender = new SmsIrOtpSender(client, Configuration());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Phone, "123456", cancellation.Token));
    }

    [Theory]
    [InlineData("SmsIr", typeof(SmsIrOtpSender))]
    [InlineData("Webhook", typeof(WebhookSmsOtpSender))]
    public void Selected_provider_reuses_the_existing_sender_boundary(string provider, Type expected)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:Issuer"] = "SenderTests",
            ["Auth:Audience"] = "SenderTests.Mobile",
            ["Auth:SigningKey"] = Convert.ToBase64String(new byte[32]),
            ["Sms:Provider"] = provider,
            ["Sms:ApiKey"] = ApiKey,
            ["Sms:TemplateId"] = "123456",
            ["Sms:Endpoint"] = "https://example.invalid/send"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration);
        using var container = services.BuildServiceProvider();
        Assert.IsType(expected, container.GetRequiredService<ISmsOtpSender>());
    }

    private static IConfiguration Configuration(string? apiKey = ApiKey, string? templateId = "123456") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sms:ApiKey"] = apiKey,
            ["Sms:TemplateId"] = templateId
        }).Build();

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/plain")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return respond(request, cancellationToken);
        }
    }
}
