namespace Flashcards.Application.Abstractions;

public interface IServiceMetadata
{
    ServiceMetadata Get();
}

public sealed record ServiceMetadata(string Name, string ApiVersion);
