using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Subscriptions;
using Flashcards.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flashcards.Infrastructure.Backups;

public sealed class BackupRestoreService(FlashcardsDbContext database, BackupService backups,
    EntitlementService entitlements, TimeProvider clock, ILogger<BackupRestoreService> logger)
{
    public async Task<BackupRestoreResult> RestoreAsync(Guid userId, Guid backupId, RestoreBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Mode != "mergeMissing" || request.ExpectedSyncVersion < 0)
            throw new BackupException(400, "invalid_restore_request", "Specify mode mergeMissing and the current non-negative sync version.");
        try
        {
            var content = await backups.ReadSnapshotAsync(userId, backupId, cancellationToken);
            var snapshot = content.Document;
            CryptographicOperations.ZeroMemory(content.Payload);
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await SyncBootstrap.LockHeadAsync(database, userId, cancellationToken);
            var head = await database.SyncHeads.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
            var version = head?.Version ?? 0;
            if (version != request.ExpectedSyncVersion)
                throw new BackupException(409, "restore_sync_conflict", "Account data changed. Synchronize and retry with the current cursor.");

            var existingDecks = (await LoadRowsAsync(snapshot.Decks.Select(item => item.Id),
                identifiers => database.Decks.AsNoTracking().Where(item => identifiers.Contains(item.Id)), cancellationToken))
                .ToDictionary(item => item.Id);
            var existingCards = (await LoadRowsAsync(snapshot.Cards.Select(item => item.Id),
                identifiers => database.Cards.AsNoTracking().Include(item => item.Deck).Where(item => identifiers.Contains(item.Id)), cancellationToken))
                .ToDictionary(item => item.Id);
            if (existingDecks.Values.Any(item => item.UserId != userId) ||
                snapshot.Cards.Any(item => existingCards.TryGetValue(item.Id, out var existing) &&
                    (existing.Deck.UserId != userId || existing.DeckId != item.DeckId)))
                throw Conflict();

            var decks = snapshot.Decks.Where(item => !existingDecks.ContainsKey(item.Id)).ToList();
            var cards = snapshot.Cards.Where(item => !existingCards.ContainsKey(item.Id)).ToList();
            var stateCards = await LoadRowsAsync(snapshot.Cards.Select(item => item.Id),
                identifiers => database.CardReviewStates.AsNoTracking().Where(item => identifiers.Contains(item.CardId))
                    .Select(item => item.CardId), cancellationToken);
            var reviewedCards = await LoadRowsAsync(snapshot.Cards.Select(item => item.Id),
                identifiers => database.ReviewEvents.AsNoTracking().Where(item => identifiers.Contains(item.CardId))
                    .Select(item => item.CardId).Distinct(), cancellationToken);
            var protectedProgress = stateCards.Concat(reviewedCards).ToHashSet();
            var states = snapshot.ReviewStates.Where(item => !protectedProgress.Contains(item.CardId)).ToList();
            var events = snapshot.ReviewEvents.Where(item => !protectedProgress.Contains(item.CardId)).ToList();
            var collisions = await LoadRowsAsync(events.Select(item => item.Id),
                identifiers => database.ReviewEvents.AsNoTracking().Where(item => identifiers.Contains(item.Id)), cancellationToken);
            if (collisions.Count != 0) throw Conflict();
            var previousOrder = (await LoadRowsAsync(events.Select(item => item.Id),
                identifiers => database.SyncChanges.AsNoTracking().Where(item => item.UserId == userId && item.EntityType == "reviewEvent" &&
                    identifiers.Contains(item.EntityId)).Select(item => new ReviewOrder(item.EntityId, item.Version)), cancellationToken))
                .GroupBy(item => item.EntityId).ToDictionary(group => group.Key, group => group.Min(item => item.Version));
            events = events.OrderBy(item => item.ServerVersion ?? (previousOrder.TryGetValue(item.Id, out var original) ? original : long.MaxValue))
                .ThenBy(item => item.ReviewedAtUtc).ThenBy(item => item.Id).ToList();
            var hasSettings = await database.UserSettings.AnyAsync(item => item.UserId == userId, cancellationToken);
            var settings = hasSettings ? [] : snapshot.Settings;
            await entitlements.EnsureRestoreAllowedAsync(userId, decks.Count(item => item.ArchivedAtUtc is null),
                cards.Count(item => item.ArchivedAtUtc is null), cancellationToken);

            var deviceId = Guid.NewGuid();
            var now = clock.GetUtcNow();
            void Record(string entityType, Guid entityId, string operation, string payload, DateTimeOffset changedAt)
            {
                version = checked(version + 1);
                database.SyncChanges.Add(new SyncChange
                {
                    UserId = userId, Version = version, DeviceId = deviceId, ClientChangeId = Guid.NewGuid(),
                    EntityType = entityType, EntityId = entityId, Operation = operation, ExpectedVersion = 0,
                    ClientChangedAtUtc = changedAt, ServerChangedAtUtc = now, PayloadJson = payload,
                    Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
                });
            }

            foreach (var saved in decks)
            {
                var deck = new Deck
                {
                    Id = saved.Id, UserId = userId, Name = saved.Name, Description = saved.Description,
                    Language = saved.Language, TextAlignment = saved.TextAlignment, TypographySize = saved.TypographySize,
                    CreatedAtUtc = saved.CreatedAtUtc, UpdatedAtUtc = saved.UpdatedAtUtc, ArchivedAtUtc = saved.ArchivedAtUtc
                };
                Record("deck", deck.Id, deck.ArchivedAtUtc is null ? "upsert" : "archive", SyncService.DeckSnapshot(deck), saved.UpdatedAtUtc);
                deck.SyncVersion = version;
                database.Decks.Add(deck);
            }
            foreach (var saved in cards)
            {
                var card = new Card
                {
                    Id = saved.Id, DeckId = saved.DeckId, FrontText = saved.FrontText, Meaning = saved.Meaning,
                    Phonetic = saved.Phonetic, Category = saved.Category, ExamplesJson = saved.ExamplesJson,
                    CreatedAtUtc = saved.CreatedAtUtc, UpdatedAtUtc = saved.UpdatedAtUtc, ArchivedAtUtc = saved.ArchivedAtUtc
                };
                Record("card", card.Id, card.ArchivedAtUtc is null ? "upsert" : "archive", SyncService.CardSnapshot(card), saved.UpdatedAtUtc);
                card.SyncVersion = version;
                database.Cards.Add(card);
            }
            foreach (var saved in events)
            {
                var review = new ReviewEvent
                {
                    Id = saved.Id, UserId = userId, DeckId = saved.DeckId, CardId = saved.CardId,
                    PreviousBox = saved.PreviousBox, NewBox = saved.NewBox, Result = saved.Result,
                    ReviewedAtUtc = saved.ReviewedAtUtc, StudySessionId = saved.StudySessionId, CreatedAtUtc = saved.CreatedAtUtc
                };
                Record("reviewEvent", review.Id, "create", JsonSerializer.Serialize(new ReviewEventPayload(review.DeckId,
                    review.CardId, review.PreviousBox, review.NewBox, review.Result, review.ReviewedAtUtc, review.StudySessionId),
                    SyncValidator.JsonOptions), saved.ReviewedAtUtc);
                database.ReviewEvents.Add(review);
            }
            foreach (var saved in states)
            {
                var state = new CardReviewState
                {
                    CardId = saved.CardId, Box = saved.Box, DueDate = saved.DueDate, LastReviewedAtUtc = saved.LastReviewedAtUtc,
                    TotalReviews = saved.TotalReviews, TotalSuccesses = saved.TotalSuccesses, ConsecutiveSuccesses = saved.ConsecutiveSuccesses,
                    CreatedAtUtc = saved.CreatedAtUtc, UpdatedAtUtc = saved.UpdatedAtUtc
                };
                Record("reviewState", state.CardId, "upsert", SyncService.StateSnapshot(state), saved.UpdatedAtUtc);
                state.SyncVersion = version;
                database.CardReviewStates.Add(state);
            }
            foreach (var saved in settings)
            {
                var restored = new UserSettings
                {
                    UserId = userId, Theme = saved.Theme, HapticsEnabled = saved.HapticsEnabled, Language = saved.Language,
                    DailyReminderEnabled = saved.DailyReminderEnabled, DailyReminderTime = saved.DailyReminderTime,
                    PreferredSpeechLanguage = saved.PreferredSpeechLanguage, PreferredSpeechAccent = saved.PreferredSpeechAccent,
                    CreatedAtUtc = saved.CreatedAtUtc, UpdatedAtUtc = saved.UpdatedAtUtc
                };
                Record("settings", userId, "upsert", SyncService.SettingsSnapshot(restored), saved.UpdatedAtUtc);
                restored.SyncVersion = version;
                database.UserSettings.Add(restored);
            }
            if (head is null) database.SyncHeads.Add(new SyncHead { UserId = userId, Version = version });
            else await database.SyncHeads.Where(item => item.UserId == userId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Version, version), cancellationToken);
            await database.SaveRestoredChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Backup {BackupId} restored using mergeMissing with {DeckCount} decks and {CardCount} cards",
                backupId, decks.Count, cards.Count);
            return new BackupRestoreResult(backupId, "mergeMissing", version, decks.Count, cards.Count, states.Count, events.Count,
                settings.Count, existingDecks.Count, existingCards.Count, hasSettings ? snapshot.Settings.Count : 0);
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            logger.LogWarning("Backup {BackupId} restore failed ({FailureType})", backupId, exception.GetType().Name);
            if (exception is BackupException or FreeTierLimitException or OperationCanceledException) throw;
            throw new BackupException(503, "backup_restore_failed", "Restore could not be confirmed. Synchronize and retry; existing records are never overwritten.", backupId);
        }
    }

    private static BackupException Conflict() => new(409, "restore_identity_conflict", "Backup identifiers conflict with existing data. Nothing was restored.");

    private sealed record ReviewOrder(Guid EntityId, long Version);

    private static async Task<List<TEntity>> LoadRowsAsync<TEntity>(IEnumerable<Guid> identifiers,
        Func<Guid[], IQueryable<TEntity>> query, CancellationToken cancellationToken)
    {
        var rows = new List<TEntity>();
        foreach (var batch in identifiers.Chunk(500)) rows.AddRange(await query(batch).ToListAsync(cancellationToken));
        return rows;
    }
}
