using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Flashcards.Infrastructure.Auth;

public sealed class SmsIrOtpSender(HttpClient client, IConfiguration configuration) : ISmsOtpSender
{
    private const string Endpoint = "https://api.sms.ir/v1/send/verify";

    private int TemplateId => int.TryParse(configuration["Sms:TemplateId"], NumberStyles.None,
        CultureInfo.InvariantCulture, out var value) ? value : 0;

    public bool IsConfigured => HasConfiguration(configuration["Sms:ApiKey"], TemplateId);

    public async Task SendAsync(string phoneNumber, string code, CancellationToken cancellationToken)
    {
        var apiKey = configuration["Sms:ApiKey"];
        var templateId = TemplateId;
        if (!HasConfiguration(apiKey, templateId))
            throw new PhoneAuthException(503, "sms_unavailable", "SMS authentication is not configured.");
        if (!Regex.IsMatch(phoneNumber, "^\\+989[0-9]{9}$", RegexOptions.CultureInvariant))
            throw new PhoneAuthException(400, "invalid_phone", "SMS authentication requires an Iranian mobile number in +989 format.");

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        request.Headers.Add("x-api-key", apiKey);
        request.Content = JsonContent.Create(new
        {
            mobile = phoneNumber[3..],
            templateId,
            parameters = new[] { new { name = "Code", value = code } }
        });
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw Unavailable();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number ||
                !status.TryGetInt32(out var statusCode) || statusCode != 1 ||
                !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("messageId", out var messageId) || messageId.ValueKind != JsonValueKind.Number ||
                !messageId.TryGetInt64(out var identifier) || identifier <= 0)
                throw Unavailable();
        }
        catch (HttpRequestException) { throw Unavailable(); }
        catch (JsonException) { throw Unavailable(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Unavailable(); }
    }

    private static bool HasConfiguration(string? apiKey, int templateId) =>
        templateId > 0 && !string.IsNullOrWhiteSpace(apiKey) && apiKey.All(character => !char.IsControl(character));

    private static PhoneAuthException Unavailable() =>
        new(503, "sms_unavailable", "SMS could not be requested. Please try again later.");
}
