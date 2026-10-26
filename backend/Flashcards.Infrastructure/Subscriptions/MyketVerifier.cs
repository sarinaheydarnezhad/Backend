using System.Net.Http.Json;
using System.Text.Json;

namespace Flashcards.Infrastructure.Subscriptions;

public sealed record VerifiedMyketPurchase(long PurchaseTime, string DeveloperPayload, int PurchaseState, int ConsumptionState);

public interface IMyketVerifier
{
    Task<VerifiedMyketPurchase> VerifyAsync(string sku, string token, CancellationToken cancellationToken);
}

public sealed class MyketVerifier(HttpClient client, MyketSettings settings) : IMyketVerifier
{
    public async Task<VerifiedMyketPurchase> VerifyAsync(string sku, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://developer.myket.ir/api/partners/applications/{Uri.EscapeDataString(settings.PackageName)}/purchases/products/{Uri.EscapeDataString(sku)}/verify");
        request.Headers.Add("X-Access-Token", settings.AccessToken);
        request.Content = JsonContent.Create(new { tokenId = token });
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new PaymentException(503, "verification_unavailable", "Unable to confirm the purchase with Myket. Retry recovery; do not pay again.");
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = body.RootElement;
            return new(root.GetProperty("purchaseTime").GetInt64(), root.GetProperty("developerPayload").GetString()!,
                root.GetProperty("purchaseState").GetInt32(), root.GetProperty("consumptionState").GetInt32());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new PaymentException(503, "verification_unavailable", "Unable to confirm the purchase with Myket. Retry recovery; do not pay again."); }
    }
}
