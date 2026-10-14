namespace Flashcards.Infrastructure.Sync;

public sealed class FreeTierLimitException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
