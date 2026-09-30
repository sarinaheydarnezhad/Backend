using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("FlashcardsDb");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<FlashcardsDbContext>(options => options.UseSqlServer(connectionString));
            services.AddScoped<FlashcardsQueries>();
        }

        return services;
    }
}
