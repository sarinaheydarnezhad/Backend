using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Sync;

internal static class SyncBootstrap
{
    internal static async Task LockHeadAsync(FlashcardsDbContext database, Guid userId, CancellationToken cancellationToken)
    {
        if (await database.SyncHeads.Where(item => item.UserId == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Version, item => item.Version), cancellationToken) == 1) return;
        if (await database.Users.Where(item => item.Id == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.UpdatedAtUtc, item => item.UpdatedAtUtc), cancellationToken) != 1)
            throw new SyncException("Account is unavailable.", statusCode: 404, code: "account_unavailable");
        if (!await database.SyncHeads.AnyAsync(item => item.UserId == userId, cancellationToken))
        {
            database.SyncHeads.Add(new SyncHead { UserId = userId });
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    internal static async Task<long> InitializeAsync(FlashcardsDbContext database, Guid userId, CancellationToken cancellationToken)
    {
        var version = await database.SyncHeads.AsNoTracking().Where(item => item.UserId == userId)
            .Select(item => (long?)item.Version).SingleOrDefaultAsync(cancellationToken) ?? 0;
        if (version != 0) return version;
        if (await database.SyncChanges.AnyAsync(item => item.UserId == userId, cancellationToken)) throw Inconsistent();
        var decks = await database.Decks.AsNoTracking().Where(item => item.UserId == userId).OrderBy(item => item.Id).ToListAsync(cancellationToken);
        var cards = await database.Cards.AsNoTracking().Where(item => item.Deck.UserId == userId).OrderBy(item => item.Id).ToListAsync(cancellationToken);
        var states = await database.CardReviewStates.AsNoTracking().Where(item => item.Card.Deck.UserId == userId)
            .OrderBy(item => item.CardId).ToListAsync(cancellationToken);
        var reviews = await database.ReviewEvents.AsNoTracking().Where(item => item.UserId == userId).ToListAsync(cancellationToken);
        var settings = await database.UserSettings.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var stateByCard = states.ToDictionary(item => item.CardId);
        if (decks.Any(item => item.SyncVersion != 0) || cards.Any(item => item.SyncVersion != 0) ||
            states.Any(item => item.SyncVersion != 0) || settings?.SyncVersion > 0 ||
            reviews.Any(item => !stateByCard.ContainsKey(item.CardId))) throw Inconsistent();
        foreach (var history in reviews.GroupBy(item => item.CardId))
        {
            var saved = stateByCard[history.Key];
            var projected = new CardReviewState();
            foreach (var review in history.OrderBy(item => item.ReviewedAtUtc).ThenBy(item => item.Id))
            {
                projected.Box = review.Result == "success" ? Math.Min(projected.Box + 1, 5) : 1;
                projected.DueDate = DateOnly.FromDateTime(review.ReviewedAtUtc.UtcDateTime)
                    .AddDays(review.Result == "success" ? 1 << (projected.Box - 1) : 0);
                projected.LastReviewedAtUtc = review.ReviewedAtUtc;
                projected.TotalReviews++;
                if (review.Result == "success")
                {
                    projected.TotalSuccesses++;
                    projected.ConsecutiveSuccesses++;
                }
                else projected.ConsecutiveSuccesses = 0;
            }
            if (saved.Box != projected.Box || saved.DueDate != projected.DueDate || saved.LastReviewedAtUtc != projected.LastReviewedAtUtc ||
                saved.TotalReviews != projected.TotalReviews || saved.TotalSuccesses != projected.TotalSuccesses ||
                saved.ConsecutiveSuccesses != projected.ConsecutiveSuccesses) throw Inconsistent();
        }

        var now = DateTimeOffset.UtcNow;
        var deviceId = Guid.NewGuid();
        void Record(string type, Guid entityId, string operation, string payload, DateTimeOffset changedAt)
        {
            version = checked(version + 1);
            database.SyncChanges.Add(new SyncChange
            {
                UserId = userId, Version = version, DeviceId = deviceId, ClientChangeId = Guid.NewGuid(),
                ClientChangedAtUtc = changedAt, ServerChangedAtUtc = now, EntityType = type, EntityId = entityId,
                Operation = operation, PayloadJson = payload, Fingerprint = "bootstrap"
            });
        }
        foreach (var deck in decks)
        {
            Record("deck", deck.Id, deck.ArchivedAtUtc is null ? "upsert" : "archive", SyncService.DeckSnapshot(deck), deck.UpdatedAtUtc);
            await database.Decks.Where(item => item.Id == deck.Id && item.UserId == userId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.SyncVersion, version), cancellationToken);
        }
        foreach (var card in cards)
        {
            Record("card", card.Id, card.ArchivedAtUtc is null ? "upsert" : "archive", SyncService.CardSnapshot(card), card.UpdatedAtUtc);
            await database.Cards.Where(item => item.Id == card.Id && item.Deck.UserId == userId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.SyncVersion, version), cancellationToken);
        }
        foreach (var review in reviews.OrderBy(item => item.ReviewedAtUtc).ThenBy(item => item.Id))
            Record("reviewEvent", review.Id, "create", JsonSerializer.Serialize(new ReviewEventPayload(review.DeckId,
                review.CardId, review.PreviousBox, review.NewBox, review.Result, review.ReviewedAtUtc, review.StudySessionId),
                SyncValidator.JsonOptions), review.ReviewedAtUtc);
        foreach (var state in states)
        {
            Record("reviewState", state.CardId, "upsert", SyncService.StateSnapshot(state), state.UpdatedAtUtc);
            await database.CardReviewStates.Where(item => item.CardId == state.CardId && item.Card.Deck.UserId == userId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.SyncVersion, version), cancellationToken);
        }
        if (settings is not null)
        {
            Record("settings", userId, "upsert", SyncService.SettingsSnapshot(settings), settings.UpdatedAtUtc);
            await database.UserSettings.Where(item => item.UserId == userId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.SyncVersion, version), cancellationToken);
        }
        if (version > 0)
        {
            await database.SyncHeads.Where(item => item.UserId == userId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Version, version), cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
        }
        return version;
    }

    private static SyncException Inconsistent() => new(
        "Account history requires an authoritative repair before synchronization. Local data must be retained.",
        code: "sync_history_incomplete", statusCode: 409);
}
