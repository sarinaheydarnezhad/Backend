using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Subscriptions;

public sealed record SubscriptionInfo(Guid Id, string Plan, string StoredStatus, string Status,
    DateTimeOffset StartsAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record LearningUsage(int Decks, int Cards);
public sealed record EntitlementResponse(string CurrentPlan, bool IsActive, DateTimeOffset? StartsAtUtc,
    DateTimeOffset? ExpiresAtUtc, PlanFeatures Features, PlanLimits Limits, bool IsPurchasable,
    bool PurchaseSupported, bool WithinFreeTierLimits, LearningUsage Usage, DateTimeOffset ValidatedAtUtc);
public sealed record SubscriptionResponse(SubscriptionInfo? Subscription, EntitlementResponse Entitlements);

public sealed class EntitlementService(FlashcardsDbContext database, PlanCatalog catalog, TimeProvider clock)
{
    public async Task<SubscriptionResponse> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var subscription = await ActiveRowAsync(userId, cancellationToken) ??
            await database.Subscriptions.AsNoTracking().Where(item => item.UserId == userId)
                .OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id)
                .FirstOrDefaultAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var status = EffectiveStatus(subscription, now);
        var plan = subscription is null ? null : catalog.Find(subscription.Plan);
        var active = status == "Active" && plan?.Limits is not null && plan.Features is not null;
        var usage = new LearningUsage(await DeckCountAsync(userId, cancellationToken), await CardCountAsync(userId, cancellationToken));
        var entitlements = new EntitlementResponse(active ? plan!.Plan : "Free", active,
            subscription?.StartsAtUtc, subscription?.ExpiresAtUtc, active ? plan!.Features! : PlanCatalog.FreeFeatures,
            active ? plan!.Limits! : PlanCatalog.FreeLimits, active && plan!.IsPurchasable, false,
            usage.Decks <= PlanCatalog.FreeLimits.Decks && usage.Cards <= PlanCatalog.FreeLimits.Cards, usage, now);
        return new SubscriptionResponse(subscription is null ? null : new SubscriptionInfo(subscription.Id,
            subscription.Plan.ToString(), subscription.Status.ToString(), status!, subscription.StartsAtUtc,
            subscription.ExpiresAtUtc, subscription.CreatedAtUtc, subscription.UpdatedAtUtc), entitlements);
    }

    public async Task EnsureDeckCreationAllowedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await EnsureAllowedAsync(userId, "deck", cancellationToken);
    }

    public async Task EnsureCardCreationAllowedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await EnsureAllowedAsync(userId, "card", cancellationToken);
    }

    public Task EnsureStudyAllowedAsync(Guid userId, CancellationToken cancellationToken = default) =>
        EnsureAllowedAsync(userId, "study", cancellationToken);

    private async Task EnsureAllowedAsync(Guid userId, string operation, CancellationToken cancellationToken)
    {
        var limits = await EffectiveLimitsAsync(userId, cancellationToken);
        var usage = new LearningUsage(await DeckCountAsync(userId, cancellationToken), await CardCountAsync(userId, cancellationToken));
        if (operation == "card" && limits.Cards is int cardMaximum && usage.Cards >= cardMaximum)
            throw new FreeTierLimitException("free_tier_card_limit", $"The free plan allows {cardMaximum} active cards in total. Existing data is preserved.", limits, usage);
        if (operation == "deck" && limits.Decks is int deckMaximum && usage.Decks >= deckMaximum)
            throw new FreeTierLimitException("free_tier_deck_limit", $"The free plan allows {deckMaximum} active deck. Existing data is preserved.", limits, usage);
        if ((limits.Decks is int decks && usage.Decks > decks) || (limits.Cards is int cards && usage.Cards > cards))
            throw new FreeTierLimitException("free_tier_dataset_limit",
                "Your data exceeds the free plan limits: 1 active deck and 50 active cards. Study and new content require Plus or explicitly reducing active data. Viewing, exporting and editing remain available.", limits, usage);
    }

    private Task<Subscription?> ActiveRowAsync(Guid userId, CancellationToken cancellationToken) =>
        database.Subscriptions.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId && item.Status == SubscriptionStatus.Active, cancellationToken);

    private async Task<PlanLimits> EffectiveLimitsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var subscription = await ActiveRowAsync(userId, cancellationToken);
        return EffectiveStatus(subscription, clock.GetUtcNow()) == "Active"
            ? catalog.Find(subscription!.Plan).Limits ?? PlanCatalog.FreeLimits
            : PlanCatalog.FreeLimits;
    }

    private Task<int> DeckCountAsync(Guid userId, CancellationToken cancellationToken) =>
        database.Decks.CountAsync(item => item.UserId == userId && item.ArchivedAtUtc == null, cancellationToken);

    private Task<int> CardCountAsync(Guid userId, CancellationToken cancellationToken) =>
        database.Cards.CountAsync(item => item.Deck.UserId == userId && item.ArchivedAtUtc == null, cancellationToken);

    private static string? EffectiveStatus(Subscription? subscription, DateTimeOffset now)
    {
        if (subscription is null) return null;
        if (subscription.Status == SubscriptionStatus.Cancelled) return "Cancelled";
        if (subscription.Status == SubscriptionStatus.Expired || now >= subscription.ExpiresAtUtc) return "Expired";
        if (now < subscription.StartsAtUtc) return "Scheduled";
        if (subscription.Plan == SubscriptionPlan.Full) return "Unavailable";
        return "Active";
    }
}
