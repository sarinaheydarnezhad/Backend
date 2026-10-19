namespace Flashcards.Infrastructure.Sync;

public sealed class FreeTierLimitException(string code, string message,
    Subscriptions.PlanLimits? limits = null, Subscriptions.LearningUsage? usage = null) : Exception(message)
{
    public string Code { get; } = code;
    public Subscriptions.PlanLimits Limits { get; } = limits ?? Subscriptions.PlanCatalog.FreeLimits;
    public Subscriptions.LearningUsage? Usage { get; } = usage;
}
