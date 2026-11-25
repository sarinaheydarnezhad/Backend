using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cent.Module;

public enum CentSmsPurpose { SignIn, PasswordReset }

public interface ICentSmsSender
{
    Task Send(string phone, string code, CentSmsPurpose purpose, CancellationToken ct);
}

// Independent implementation of Melipayamak console's shared-template API.
public sealed class CentMelipayamak(HttpClient client, IConfiguration config, ILogger<CentMelipayamak> log) : ICentSmsSender
{
    public async Task Send(string phone, string code, CentSmsPurpose purpose, CancellationToken ct)
    {
        const string root = "Cent:Sms:Melipayamak:";
        var key = config[root + "ApiKey"] ?? "";
        var endpoint = config[root + "Endpoint"] ?? "https://console.melipayamak.com/api/send/shared/";
        // This API requires its key in the provider path. HTTP-client logging is disabled in DI.
        var pattern = purpose == CentSmsPurpose.PasswordReset ? "RecoveryBodyId" : "BodyId";
        if (!Regex.IsMatch(key, "\\A[A-Za-z0-9_-]{8,256}\\z") ||
            !int.TryParse(config[root + pattern], out var bodyId) || bodyId <= 0 ||
            endpoint != "https://console.melipayamak.com/api/send/shared/") throw Unavailable();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.GetValue(root + "TimeoutSeconds", 10), 1, 30)));
        try {
            // No retries: timeout can mean the provider accepted a message without returning a receipt.
            using var response = await client.PostAsJsonAsync(endpoint + key, new { bodyId, to = "0" + phone[3..], args = new[] { code } }, timeout.Token);
            if (!response.IsSuccessStatusCode) {
                log.LogWarning("Cent SMS provider HTTP failure: {Status}", (int)response.StatusCode);
                throw Unavailable();
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await CentGooglePublicKeys.ReadBounded(stream, 8192, timeout.Token);
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("recId", out var receipt) ||
                !long.TryParse(receipt.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) {
                log.LogWarning("Cent SMS provider rejected template delivery (response redacted)");
                throw Unavailable();
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException or CentBodyTooLargeException || e is OperationCanceledException && !ct.IsCancellationRequested) {
            log.LogWarning("Cent SMS delivery failed: {Type}", e.GetType().Name);
            throw Unavailable();
        }
    }
    public static CentException Unavailable() => new(503, "sms_unavailable", "ارسال پیامک فعلاً ممکن نیست. کمی بعد دوباره تلاش کنید.");
}

internal static class CentSmsRegistration
{
    internal static void AddCentSms(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<CentSmsClock>();
        services.AddScoped<CentSmsAuth>();
        services.AddHttpClient<ICentSmsSender, CentMelipayamak>(c => c.Timeout = TimeSpan.FromSeconds(35))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    }
}
