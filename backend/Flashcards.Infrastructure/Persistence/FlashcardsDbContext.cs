using Flashcards.Domain;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Persistence;

public sealed class FlashcardsDbContext(DbContextOptions<FlashcardsDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Deck> Decks => Set<Deck>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<CardReviewState> CardReviewStates => Set<CardReviewState>();
    public DbSet<ReviewEvent> ReviewEvents => Set<ReviewEvent>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.DisplayName).HasMaxLength(120).IsRequired();
            entity.HasIndex(item => item.UpdatedAtUtc);
            ConfigureDates(entity);
        });

        modelBuilder.Entity<Deck>(entity =>
        {
            entity.ToTable("Decks", table =>
            {
                table.HasCheckConstraint("CK_Decks_TextAlignment", "[TextAlignment] IN ('ltr', 'rtl', 'center')");
                table.HasCheckConstraint("CK_Decks_TypographySize", "[TypographySize] IN ('small', 'medium', 'large')");
            });
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.UserId, item.Id });
            entity.HasOne(item => item.User).WithMany(item => item.Decks).HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.NoAction);
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(1000).IsRequired();
            entity.Property(item => item.Language).HasMaxLength(35).IsUnicode(false).IsRequired();
            entity.Property(item => item.TextAlignment).HasMaxLength(6).IsUnicode(false).IsRequired();
            entity.Property(item => item.TypographySize).HasMaxLength(6).IsUnicode(false).IsRequired();
            entity.HasIndex(item => new { item.UserId, item.ArchivedAtUtc, item.Name });
            entity.HasIndex(item => new { item.UserId, item.UpdatedAtUtc, item.Id });
            ConfigureDates(entity);
        });

        modelBuilder.Entity<Card>(entity =>
        {
            entity.ToTable("Cards", table => table.HasCheckConstraint("CK_Cards_ExamplesJson", "ISJSON([ExamplesJson]) = 1"));
            entity.HasKey(item => item.Id);
            entity.HasAlternateKey(item => new { item.DeckId, item.Id });
            entity.HasOne(item => item.Deck).WithMany(item => item.Cards).HasForeignKey(item => item.DeckId).OnDelete(DeleteBehavior.NoAction);
            entity.Property(item => item.FrontText).HasMaxLength(4000).IsRequired();
            entity.Property(item => item.Phonetic).HasMaxLength(500);
            entity.Property(item => item.Category).HasMaxLength(120);
            entity.Property(item => item.Meaning).HasMaxLength(4000).IsRequired();
            entity.Property(item => item.ExamplesJson).IsRequired();
            entity.HasIndex(item => new { item.DeckId, item.ArchivedAtUtc, item.Id });
            entity.HasIndex(item => new { item.DeckId, item.UpdatedAtUtc, item.Id });
            ConfigureDates(entity);
        });

        modelBuilder.Entity<CardReviewState>(entity =>
        {
            entity.ToTable("CardReviewStates", table =>
            {
                table.HasCheckConstraint("CK_ReviewStates_Box", "[Box] BETWEEN 1 AND 5");
                table.HasCheckConstraint("CK_ReviewStates_Counts", "[ConsecutiveSuccesses] >= 0 AND [TotalReviews] >= 0 AND [TotalSuccesses] >= 0 AND [TotalSuccesses] <= [TotalReviews] AND [ConsecutiveSuccesses] <= [TotalSuccesses]");
            });
            entity.HasKey(item => item.CardId);
            entity.HasOne(item => item.Card).WithOne(item => item.ReviewState).HasForeignKey<CardReviewState>(item => item.CardId).OnDelete(DeleteBehavior.NoAction);
            entity.Property(item => item.DueDate).HasColumnType("date");
            entity.HasIndex(item => new { item.DueDate, item.CardId });
            entity.HasIndex(item => new { item.UpdatedAtUtc, item.CardId });
            ConfigureDates(entity);
        });

        modelBuilder.Entity<ReviewEvent>(entity =>
        {
            entity.ToTable("ReviewEvents", table =>
            {
                table.HasTrigger("TR_ReviewEvents_Immutable");
                table.HasCheckConstraint("CK_ReviewEvents_Boxes", "[PreviousBox] BETWEEN 1 AND 5 AND [NewBox] BETWEEN 1 AND 5");
                table.HasCheckConstraint("CK_ReviewEvents_Result", "[Result] IN ('success', 'failure')");
            });
            entity.HasKey(item => item.Id);
            entity.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(item => item.Deck).WithMany().HasForeignKey(item => new { item.UserId, item.DeckId })
                .HasPrincipalKey(item => new { item.UserId, item.Id }).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(item => item.Card).WithMany().HasForeignKey(item => new { item.DeckId, item.CardId })
                .HasPrincipalKey(item => new { item.DeckId, item.Id }).OnDelete(DeleteBehavior.NoAction);
            entity.Property(item => item.Result).HasMaxLength(7).IsUnicode(false).IsRequired();
            entity.HasIndex(item => new { item.UserId, item.ReviewedAtUtc, item.Id });
            entity.HasIndex(item => new { item.CardId, item.ReviewedAtUtc });
            entity.HasIndex(item => new { item.DeckId, item.ReviewedAtUtc });
            entity.HasIndex(item => new { item.UserId, item.CreatedAtUtc, item.Id });
            entity.Property(item => item.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
            entity.Property(item => item.ReviewedAtUtc).HasColumnType("datetimeoffset(7)");
        });

        modelBuilder.Entity<UserSettings>(entity =>
        {
            entity.ToTable("UserSettings", table =>
            {
                table.HasCheckConstraint("CK_UserSettings_Theme", "[Theme] IN ('system', 'light', 'dark', 'oled')");
                table.HasCheckConstraint("CK_UserSettings_Accent", "[PreferredSpeechAccent] IS NULL OR [PreferredSpeechAccent] IN ('us', 'uk')");
                table.HasCheckConstraint("CK_UserSettings_Reminder", "[DailyReminderEnabled] = 0 OR [DailyReminderTime] IS NOT NULL");
            });
            entity.HasKey(item => item.UserId);
            entity.HasOne(item => item.User).WithOne(item => item.Settings).HasForeignKey<UserSettings>(item => item.UserId).OnDelete(DeleteBehavior.NoAction);
            entity.Property(item => item.Theme).HasMaxLength(6).IsUnicode(false).IsRequired();
            entity.Property(item => item.Language).HasMaxLength(35).IsUnicode(false).IsRequired();
            entity.Property(item => item.PreferredSpeechLanguage).HasMaxLength(35).IsUnicode(false).IsRequired();
            entity.Property(item => item.PreferredSpeechAccent).HasMaxLength(2).IsUnicode(false);
            entity.Property(item => item.DailyReminderTime).HasColumnType("time(0)");
            entity.HasIndex(item => item.UpdatedAtUtc);
            ConfigureDates(entity);
        });
    }

    private static void ConfigureDates<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : class
    {
        entity.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("datetimeoffset(7)");
        entity.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("datetimeoffset(7)");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampChanges()
    {
        ChangeTracker.DetectChanges();
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is ReviewEvent && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Review events are immutable.");

            if (entry.State == EntityState.Added)
            {
                entry.Property("CreatedAtUtc").CurrentValue = now;
                if (entry.Entity is ReviewEvent) continue;
            }

            if (entry.State is EntityState.Added or EntityState.Modified && entry.Entity is not ReviewEvent)
            {
                entry.Property("UpdatedAtUtc").CurrentValue = now;
                if (entry.State == EntityState.Modified)
                    entry.Property("CreatedAtUtc").IsModified = false;
            }
        }
    }
}
