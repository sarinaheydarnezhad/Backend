namespace Flashcards.Domain;

public sealed class MyketOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Sku { get; set; } = "";
    public int DurationMonths { get; set; }
    public int PriceToman { get; set; }
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public bool Cancelled { get; set; }
    public string? TokenHash { get; set; }
    public Guid? SubscriptionId { get; set; }
}
