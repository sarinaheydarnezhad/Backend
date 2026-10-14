using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace Flashcards.Infrastructure.Auth;

public interface ISmsOtpSender
{
    bool IsConfigured { get; }
    Task SendAsync(string phoneNumber, string code, CancellationToken cancellationToken);
}

public sealed class WebhookSmsOtpSender(HttpClient client, IConfiguration configuration) : ISmsOtpSender
{
    public bool IsConfigured => Uri.TryCreate(configuration["Sms:Endpoint"], UriKind.Absolute, out var endpoint) &&
        endpoint.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(configuration["Sms:ApiKey"]);

    public async Task SendAsync(string phoneNumber, string code, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new PhoneAuthException(503, "sms_unavailable", "SMS authentication is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, configuration["Sms:Endpoint"]);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["Sms:ApiKey"]);
        request.Content = JsonContent.Create(new { phoneNumber, code });
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new PhoneAuthException(503, "sms_unavailable", "SMS could not be requested. Please try again later.");
        }
        catch (HttpRequestException)
        {
            throw new PhoneAuthException(503, "sms_unavailable", "SMS could not be requested. Please try again later.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PhoneAuthException(503, "sms_unavailable", "SMS could not be requested. Please try again later.");
        }
    }
}
