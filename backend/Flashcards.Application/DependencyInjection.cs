using Flashcards.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IServiceMetadata, StaticServiceMetadata>();
        return services;
    }

    private sealed class StaticServiceMetadata : IServiceMetadata
    {
        public ServiceMetadata Get() => new("Flashcards API", "v1");
    }
}
