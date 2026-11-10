using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Flashcards.Infrastructure.Auth;

// Official contract: https://kavenegar.com/rest.html#verifylookup
public sealed class KavenegarOtpSender(HttpClient client, IConfiguration configuration) : ISmsOtpSender
{
    public bool IsConfigured => IsSafe(configuration["Kavenegar:ApiKey"], 512) &&
        configuration["Kavenegar:ApiKey"] != "PASTE_MY_KAVENEGAR_API_KEY_HERE" &&
        configuration["Kavenegar:ApiKey"] != "YOUR_KAVENEGAR_API_KEY" &&
        IsSafe(configuration["Kavenegar:OtpTemplate"], 100) &&
        configuration["Kavenegar:OtpTemplate"] != "YOUR_APPROVED_TEMPLATE_NAME";

    public async Task ValidateLiveConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw Unavailable();
        try
        {
            using var response = await client.GetAsync(Endpoint("account/config"), cancellationToken);
            if (!response.IsSuccessStatusCode) throw Unavailable();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (!IsAccepted(result) || !result.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Object || !entries.TryGetProperty("debugmode", out var debug) ||
                debug.ValueKind != JsonValueKind.String || debug.GetString() != "disabled")
                throw Unavailable();
        }
        catch (HttpRequestException) { throw Unavailable(); }
        catch (JsonException) { throw Unavailable(); }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw Unavailable();
        }
    }

    public async Task SendAsync(string phoneNumber, string code, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw Unavailable();
        if (!Regex.IsMatch(phoneNumber, "^\\+989[0-9]{9}$", RegexOptions.CultureInvariant))
            throw new PhoneAuthException(400, "invalid_phone", "Enter a valid Iranian mobile number.");

        // Read-only preflight: a provider debug account must never authenticate users.
        await ValidateLiveConfigurationAsync(cancellationToken);

        // POST keeps the OTP and phone out of the URL. The provider requires the key in its path.
        var endpoint = Endpoint("verify/lookup");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["receptor"] = "0" + phoneNumber[3..],
                ["token"] = code,
                ["template"] = configuration["Kavenegar:OtpTemplate"]!,
                ["type"] = "sms"
            })
        };
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw Unavailable();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (!IsAccepted(result) ||
                !result.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
                entries.GetArrayLength() != 1 || entries[0].ValueKind != JsonValueKind.Object ||
                !entries[0].TryGetProperty("messageid", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt64(out var messageId) || messageId <= 0 ||
                !entries[0].TryGetProperty("status", out var messageStatus) || messageStatus.ValueKind != JsonValueKind.Number ||
                !messageStatus.TryGetInt32(out var deliveryStatus) || deliveryStatus is not (1 or 2 or 4 or 5 or 10))
                throw Unavailable();
        }
        // Never propagate provider bodies, URLs or exceptions containing API keys.
        catch (HttpRequestException) { throw Unavailable(); }
        catch (JsonException) { throw Unavailable(); }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw Unavailable();
        }
    }

    private static bool IsSafe(string? value, int maxLength) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxLength && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool IsNumber(JsonElement value, int expected) => value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) && number == expected;
    private static bool IsAccepted(JsonElement result) => result.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty("return", out var outcome) && outcome.ValueKind == JsonValueKind.Object &&
        outcome.TryGetProperty("status", out var status) && IsNumber(status, 200);
    private string Endpoint(string method) =>
        $"https://api.kavenegar.com/v1/{Uri.EscapeDataString(configuration["Kavenegar:ApiKey"]!)}/{method}.json";
    private static PhoneAuthException Unavailable() =>
        new(503, "sms_unavailable", "SMS could not be requested. Please try again later.");
}
