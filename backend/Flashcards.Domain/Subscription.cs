namespace Flashcards.Domain;

public enum SubscriptionPlan { Plus, Full }
public enum SubscriptionStatus { Active, Expired, Cancelled }

public sealed class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public SubscriptionPlan Plan { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Cancelled;
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
