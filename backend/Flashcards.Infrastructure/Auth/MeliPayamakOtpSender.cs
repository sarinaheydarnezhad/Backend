using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Flashcards.Infrastructure.Auth;

public sealed class MeliPayamakOtpSender(HttpClient client, IConfiguration configuration) : ISmsOtpSender
{
    private const string Endpoint = "https://console.melipayamak.com/api/send/shared/";

    public bool IsConfigured => GetApiKey() is not null &&
        GetBodyId("RegistrationBodyId") > 0 && GetBodyId("ResetBodyId") > 0;

    public Task SendAsync(string phoneNumber, string code, CancellationToken cancellationToken) =>
        SendAsync(phoneNumber, code, "register", cancellationToken);

    public async Task SendAsync(string phoneNumber, string code, string purpose, CancellationToken cancellationToken)
    {
        var apiKey = GetApiKey();
        var bodyId = purpose switch
        {
            "register" => GetBodyId("RegistrationBodyId"),
            "reset" => GetBodyId("ResetBodyId"),
            _ => 0
        };
        if (apiKey is null || bodyId <= 0)
            throw new PhoneAuthException(503, "sms_unavailable", "SMS authentication is not configured.");
        if (!Regex.IsMatch(phoneNumber, @"\A\+989[0-9]{9}\z", RegexOptions.CultureInvariant))
            throw new PhoneAuthException(400, "invalid_phone", "SMS authentication requires an Iranian mobile number in +989 format.");

        try
        {
            using var response = await client.PostAsJsonAsync(Endpoint + apiKey, new
            {
                bodyId,
                to = "0" + phoneNumber[3..],
                args = new[] { code }
            }, cancellationToken);
            if (!response.IsSuccessStatusCode) throw Unavailable();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("recId", out var recId) ||
                !HasPositiveReceipt(recId))
                throw Unavailable();
        }
        catch (HttpRequestException) { throw Unavailable(); }
        catch (JsonException) { throw Unavailable(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Unavailable(); }
    }

    private string? GetApiKey()
    {
        var value = configuration["MeliPayamak:ApiKey"];
        return value is not null && Regex.IsMatch(value, @"\A[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant)
            ? value : null;
    }

    private int GetBodyId(string key) => int.TryParse(configuration[$"MeliPayamak:{key}"],
        NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static bool HasPositiveReceipt(JsonElement receipt) => receipt.ValueKind switch
    {
        JsonValueKind.Number => receipt.TryGetInt64(out var number) && number > 0,
        JsonValueKind.String => long.TryParse(receipt.GetString(), NumberStyles.None,
            CultureInfo.InvariantCulture, out var number) && number > 0,
        _ => false
    };

    private static PhoneAuthException Unavailable() =>
        new(503, "sms_unavailable", "SMS could not be requested. Please try again later.");
}
