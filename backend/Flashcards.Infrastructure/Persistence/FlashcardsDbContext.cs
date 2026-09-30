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
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<SyncHead> SyncHeads => Set<SyncHead>();
    public DbSet<SyncChange> SyncChanges => Set<SyncChange>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.DisplayName).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Email).HasMaxLength(256);
            entity.Property(item => item.NormalizedEmail).HasMaxLength(256);
            entity.Property(item => item.PasswordHash).HasMaxLength(512);
            entity.HasIndex(item => item.NormalizedEmail).IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
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
            entity.Property(item => item.SyncVersion).HasDefaultValue(0L);
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
            entity.Property(item => item.SyncVersion).HasDefaultValue(0L);
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
            entity.Property(item => item.SyncVersion).HasDefaultValue(0L);
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
            entity.Property(item => item.SyncVersion).HasDefaultValue(0L);
            ConfigureDates(entity);
        });

        modelBuilder.Entity<AuthSession>(entity =>
        {
            entity.ToTable("AuthSessions");
            entity.HasKey(item => item.Id);
            entity.HasOne(item => item.User).WithMany(item => item.AuthSessions)
                .HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(item => new { item.UserId, item.RevokedAtUtc });
            entity.Property(item => item.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
            entity.Property(item => item.ExpiresAtUtc).HasColumnType("datetimeoffset(7)");
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens");
            entity.HasKey(item => item.Id);
            entity.HasOne(item => item.AuthSession).WithMany(item => item.RefreshTokens)
                .HasForeignKey(item => item.AuthSessionId).OnDelete(DeleteBehavior.NoAction);
            entity.Property(item => item.TokenHash).HasMaxLength(64).IsUnicode(false).IsRequired();
            entity.HasIndex(item => item.TokenHash).IsUnique();
            entity.HasIndex(item => new { item.AuthSessionId, item.ConsumedAtUtc });
            entity.Property(item => item.CreatedAtUtc).HasColumnType("datetimeoffset(7)");
            entity.Property(item => item.ExpiresAtUtc).HasColumnType("datetimeoffset(7)");
        });

        modelBuilder.Entity<SyncHead>(entity =>
        {
            entity.ToTable("SyncHeads", table => table.HasCheckConstraint("CK_SyncHeads_Version", "[Version] >= 0"));
            entity.HasKey(item => item.UserId);
            entity.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<SyncChange>(entity =>
        {
            entity.ToTable("SyncChanges", table =>
            {
                table.HasTrigger("TR_SyncChanges_Immutable");
                table.HasCheckConstraint("CK_SyncChanges_Version", "[Version] > 0 AND [ExpectedVersion] >= 0");
                table.HasCheckConstraint("CK_SyncChanges_Payload", "ISJSON([PayloadJson]) = 1");
            });
            entity.HasKey(item => new { item.UserId, item.Version });
            entity.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(item => new { item.UserId, item.DeviceId, item.ClientChangeId }).IsUnique();
            entity.HasIndex(item => new { item.UserId, item.EntityType, item.EntityId, item.Version });
            entity.Property(item => item.EntityType).HasMaxLength(24).IsUnicode(false).IsRequired();
            entity.Property(item => item.Operation).HasMaxLength(12).IsUnicode(false).IsRequired();
            entity.Property(item => item.PayloadJson).IsRequired();
            entity.Property(item => item.Fingerprint).HasMaxLength(64).IsUnicode(false).IsRequired();
            entity.Property(item => item.ClientChangedAtUtc).HasColumnType("datetimeoffset(7)");
            entity.Property(item => item.ServerChangedAtUtc).HasColumnType("datetimeoffset(7)");
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
            if (entry.Entity is ReviewEvent or SyncChange && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Historical records are immutable.");

            if (entry.State == EntityState.Added && entry.Entity is not (SyncHead or SyncChange))
            {
                entry.Property("CreatedAtUtc").CurrentValue = now;
                if (entry.Entity is ReviewEvent) continue;
            }

            if (entry.Entity is AuthSession or RefreshToken && entry.State == EntityState.Modified)
                entry.Property("CreatedAtUtc").IsModified = false;

            if (entry.State is EntityState.Added or EntityState.Modified && entry.Entity is User or Deck or Card or CardReviewState or Flashcards.Domain.UserSettings)
            {
                entry.Property("UpdatedAtUtc").CurrentValue = now;
                if (entry.State == EntityState.Modified)
                    entry.Property("CreatedAtUtc").IsModified = false;
            }
        }
    }
}
