using System.Data;
using System.Security.Cryptography;
using System.Text;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Subscriptions;

public sealed class PaymentException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record MyketConfiguration(string PublicKey, string PackageName, IReadOnlyList<string> Skus);
public sealed record MyketCheckout(Guid Id, string Sku, string DeveloperPayload, int DurationMonths,
    int PriceToman, DateTimeOffset ExpiresAtUtc);
public sealed record VerifyMyketRequest(string Sku, string Token);

public sealed class MyketPaymentService(FlashcardsDbContext database, MyketSettings settings,
    PlanCatalog catalog, EntitlementService entitlements, IMyketVerifier verifier, TimeProvider clock)
{
    public MyketConfiguration Configuration()
    {
        EnsureConfigured();
        return new(settings.PublicKey, settings.PackageName, settings.Products.Values.ToArray());
    }

    public async Task<MyketCheckout> BeginAsync(Guid userId, int months, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var price = catalog.Find(SubscriptionPlan.Plus).Prices.SingleOrDefault(item => item.DurationMonths == months)
            ?? throw new PaymentException(400, "invalid_period", "Unsupported Plus duration.");
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await LockOwnerAsync(userId, cancellationToken);
        await EnsureNoActiveAsync(userId, cancellationToken);
        var now = clock.GetUtcNow();
        var order = await database.MyketOrders.SingleOrDefaultAsync(item => item.UserId == userId &&
            !item.Cancelled && item.SubscriptionId == null && item.ExpiresAtUtc > now, cancellationToken);
        if (order is not null && order.DurationMonths != months)
            throw new PaymentException(409, "checkout_pending", "Retry the pending checkout using its original duration, or wait one hour before changing duration.");
        if (order is null)
        {
            order = new MyketOrder { UserId = userId, Sku = settings.Products[months], DurationMonths = months,
                PriceToman = price.PriceToman, Payload = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                CreatedAtUtc = now, ExpiresAtUtc = now.AddHours(1) };
            database.MyketOrders.Add(order);
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(order.Id, order.Sku, order.Payload, order.DurationMonths, order.PriceToman, order.ExpiresAtUtc);
    }

    public async Task CancelAsync(Guid userId, Guid id, CancellationToken cancellationToken = default)
    {
        await database.MyketOrders.Where(item => item.Id == id && item.UserId == userId && item.SubscriptionId == null)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Cancelled, true), cancellationToken);
    }

    public async Task<SubscriptionResponse> VerifyAsync(Guid userId, VerifyMyketRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(request.Sku) || request.Sku.Length > 200 ||
            string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 4096) throw InvalidReceipt();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token)));
        var replay = await database.MyketOrders.AsNoTracking().SingleOrDefaultAsync(item => item.TokenHash == hash, cancellationToken);
        if (replay is not null)
        {
            if (replay.UserId != userId || replay.Sku != request.Sku) throw InvalidReceipt();
            return await entitlements.GetAsync(userId, cancellationToken);
        }
        if (!settings.Products.Values.Contains(request.Sku, StringComparer.Ordinal)) throw InvalidReceipt();
        var verified = await verifier.VerifyAsync(request.Sku, request.Token, cancellationToken);
        if (verified.PurchaseState != 0 || verified.ConsumptionState != 0 ||
            string.IsNullOrWhiteSpace(verified.DeveloperPayload)) throw InvalidReceipt();
        DateTimeOffset purchased;
        try { purchased = DateTimeOffset.FromUnixTimeMilliseconds(verified.PurchaseTime); }
        catch (ArgumentOutOfRangeException) { throw InvalidReceipt(); }
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await LockOwnerAsync(userId, cancellationToken);
        var order = await database.MyketOrders.SingleOrDefaultAsync(item => item.Payload == verified.DeveloperPayload &&
            item.UserId == userId, cancellationToken) ?? throw InvalidReceipt();
        if (order.Sku != request.Sku || purchased < order.CreatedAtUtc.AddMinutes(-2) ||
            purchased > clock.GetUtcNow()) throw InvalidReceipt();
        if (order.SubscriptionId is not null)
        {
            if (order.TokenHash != hash) throw InvalidReceipt();
            await transaction.CommitAsync(cancellationToken);
            return await entitlements.GetAsync(userId, cancellationToken);
        }
        await EnsureNoActiveAsync(userId, cancellationToken);
        var previous = await database.Subscriptions.Where(item => item.UserId == userId &&
            item.Status == SubscriptionStatus.Active).ToListAsync(cancellationToken);
        foreach (var subscription in previous) subscription.Status = SubscriptionStatus.Expired;
        await database.SaveChangesAsync(cancellationToken);
        var expires = purchased.AddMonths(order.DurationMonths);
        var activation = new Subscription { UserId = userId, Plan = SubscriptionPlan.Plus,
            Status = expires > clock.GetUtcNow() ? SubscriptionStatus.Active : SubscriptionStatus.Expired,
            StartsAtUtc = purchased, ExpiresAtUtc = expires };
        database.Subscriptions.Add(activation);
        order.TokenHash = hash;
        order.SubscriptionId = activation.Id;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await entitlements.GetAsync(userId, cancellationToken);
    }

    private Task<int> LockOwnerAsync(Guid userId, CancellationToken cancellationToken) =>
        database.Users.Where(item => item.Id == userId).ExecuteUpdateAsync(
            update => update.SetProperty(item => item.UpdatedAtUtc, item => item.UpdatedAtUtc), cancellationToken);

    private async Task EnsureNoActiveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (await database.Subscriptions.AnyAsync(item => item.UserId == userId &&
                item.Status == SubscriptionStatus.Active && item.ExpiresAtUtc > now, cancellationToken))
            throw new PaymentException(409, "subscription_active", "Another subscription cannot be purchased while access is active or scheduled.");
    }

    private void EnsureConfigured()
    {
        if (!settings.IsConfigured)
            throw new PaymentException(503, "payments_unavailable", "Myket payments are not configured.");
    }

    private static PaymentException InvalidReceipt() => new(400, "invalid_receipt", "Purchase verification failed. The purchase was not consumed.");
}
