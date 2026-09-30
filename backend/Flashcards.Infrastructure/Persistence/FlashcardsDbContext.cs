using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Persistence;

public sealed class FlashcardsDbContext(DbContextOptions<FlashcardsDbContext> options) : DbContext(options);
