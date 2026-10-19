using Flashcards.Domain;

namespace Flashcards.Infrastructure.Subscriptions;

public sealed record PlanLimits(int? Decks, int? Cards);
public sealed record PlanFeatures(bool UnlimitedDecks, bool UnlimitedCards);
public sealed record PlanPrice(int DurationMonths, int PriceToman);
public sealed record PlanDefinition(string Plan, bool IsPurchasable, string UnavailableReason,
    PlanLimits? Limits, PlanFeatures? Features, IReadOnlyList<PlanPrice> Prices);
public sealed record PlanCatalogResponse(string Currency, bool PurchaseSupported, PlanLimits FreeLimits,
    IReadOnlyList<PlanDefinition> Plans);

public sealed class PlanCatalog
{
    public static readonly PlanLimits FreeLimits = new(1, 50);
    public static readonly PlanFeatures FreeFeatures = new(false, false);
    private static readonly PlanDefinition Plus = new("Plus", false, "payment_provider_not_configured",
        new PlanLimits(null, null), new PlanFeatures(true, true), Array.AsReadOnly(new[]
        {
            new PlanPrice(1, 49000), new PlanPrice(6, 279000), new PlanPrice(12, 499000)
        }));
    private static readonly PlanDefinition Full = new("Full", false, "coming_soon", null, null,
        Array.AsReadOnly(Array.Empty<PlanPrice>()));

    public PlanDefinition Find(SubscriptionPlan plan) => plan switch
    {
        SubscriptionPlan.Plus => Plus,
        SubscriptionPlan.Full => Full,
        _ => throw new InvalidOperationException("Unsupported subscription plan.")
    };

    public PlanCatalogResponse Get() => new("Toman", false, FreeLimits, Array.AsReadOnly(new[] { Plus, Full }));
}
