using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Flashcards.Infrastructure.Persistence;

public sealed class FlashcardsDbContextFactory : IDesignTimeDbContextFactory<FlashcardsDbContext>
{
    public FlashcardsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__FlashcardsDb")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=FlashcardsDev;Trusted_Connection=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<FlashcardsDbContext>().UseSqlServer(connectionString).Options;
        return new FlashcardsDbContext(options);
    }
}
