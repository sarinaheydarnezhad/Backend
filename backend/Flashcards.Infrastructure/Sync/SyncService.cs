using System.Data;
using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Sync;

public sealed class SyncService(FlashcardsDbContext database)
{
    public async Task<SyncPushResponse> PushAsync(Guid userId, SyncPushRequest request, CancellationToken cancellationToken = default)
    {
        var changes = SyncValidator.Validate(request);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var head = await database.SyncHeads.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (head is null)
        {
            database.SyncHeads.Add(new SyncHead { UserId = userId });
            await database.SaveChangesAsync(cancellationToken);
        }

        await database.SyncHeads.Where(item => item.UserId == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Version, item => item.Version), cancellationToken);
        var version = await database.SyncHeads.AsNoTracking()
            .Where(item => item.UserId == userId).Select(item => item.Version).SingleAsync(cancellationToken);
        var acknowledgements = new List<SyncPushAcknowledgement>(changes.Count);

        foreach (var validated in changes)
        {
            var change = validated.Change;
            var receipt = await database.SyncChanges.AsNoTracking().SingleOrDefaultAsync(item =>
                item.UserId == userId && item.DeviceId == change.DeviceId && item.ClientChangeId == change.ClientChangeId,
                cancellationToken);
            if (receipt is not null)
            {
                if (receipt.Fingerprint != validated.Fingerprint)
                    throw await ConflictAsync(userId, change, receipt.Version, receipt.PayloadJson, cancellationToken);
                acknowledgements.Add(new SyncPushAcknowledgement(change.ClientChangeId, "duplicate", receipt.Version));
                continue;
            }

            if (change.EntityType == "reviewEvent")
            {
                var previous = await database.SyncChanges.AsNoTracking().FirstOrDefaultAsync(item =>
                    item.UserId == userId && item.EntityType == "reviewEvent" && item.EntityId == change.EntityId,
                    cancellationToken);
                if (previous is not null)
                {
                    if (previous.PayloadJson != JsonSerializer.Serialize(validated.Payload, SyncValidator.JsonOptions))
                        throw await ConflictAsync(userId, change, previous.Version, previous.PayloadJson, cancellationToken);
                    acknowledgements.Add(new SyncPushAcknowledgement(change.ClientChangeId, "duplicate", previous.Version));
                    continue;
                }
            }

            var nextVersion = checked(version + 1);
            var snapshot = await ApplyAsync(userId, validated, nextVersion, cancellationToken);
            var updated = await database.SyncHeads.Where(item => item.UserId == userId && item.Version == version)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Version, nextVersion), cancellationToken);
            if (updated != 1) throw new SyncException("Concurrent sync change. Retry the push.");

            database.SyncChanges.Add(new SyncChange
            {
                UserId = userId, Version = nextVersion, DeviceId = change.DeviceId,
                ClientChangeId = change.ClientChangeId, ClientChangedAtUtc = change.ClientChangedAtUtc.ToUniversalTime(),
                ServerChangedAtUtc = DateTimeOffset.UtcNow, EntityType = change.EntityType, EntityId = change.EntityId,
                Operation = change.Operation, ExpectedVersion = change.ExpectedVersion,
                PayloadJson = snapshot, Fingerprint = validated.Fingerprint
            });
            await database.SaveChangesAsync(cancellationToken);
            version = nextVersion;
            acknowledgements.Add(new SyncPushAcknowledgement(change.ClientChangeId, "applied", version));
        }

        await transaction.CommitAsync(cancellationToken);
        return new SyncPushResponse(version, acknowledgements);
    }

    public async Task<SyncPullResponse> PullAsync(Guid userId, long cursor, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (cursor < 0 || limit is < 1 or > 100) throw new SyncException("Invalid cursor or page size.");
        var latest = await database.SyncHeads.AsNoTracking()
            .Where(item => item.UserId == userId).Select(item => (long?)item.Version).SingleOrDefaultAsync(cancellationToken) ?? 0;
        if (cursor > latest) throw new SyncException("Cursor is ahead of the server version.");

        var page = await database.SyncChanges.AsNoTracking()
            .Where(item => item.UserId == userId && item.Version > cursor && item.Version <= latest)
            .OrderBy(item => item.Version).Take(limit).ToListAsync(cancellationToken);
        var nextCursor = page.Count == 0 ? cursor : page[^1].Version;
        var changes = page.Select(item => new SyncPullChange(
            item.EntityType, item.EntityId, item.Operation, item.DeviceId, item.ClientChangeId,
            item.ClientChangedAtUtc, item.ServerChangedAtUtc, item.ExpectedVersion, item.Version, ParseJson(item.PayloadJson))).ToList();
        return new SyncPullResponse(nextCursor, latest, nextCursor < latest, changes);
    }

    private async Task<string> ApplyAsync(Guid userId, ValidatedSyncChange validated, long version, CancellationToken cancellationToken)
    {
        var change = validated.Change;
        switch (change.EntityType)
        {
            case "deck":
            {
                var deck = await database.Decks.SingleOrDefaultAsync(item => item.Id == change.EntityId, cancellationToken);
                if (deck is not null && deck.UserId != userId)
                    throw new SyncException("Entity ID is unavailable.", new SyncConflict("deck", change.EntityId, change.ExpectedVersion, 0, null, ClientPayload(change)));
                if (deck is null)
                {
                    if (change.Operation != "upsert" || change.ExpectedVersion != 0)
                        throw await ConflictAsync(userId, change, 0, null, cancellationToken);
                    deck = new Deck { Id = change.EntityId, UserId = userId };
                    database.Decks.Add(deck);
                }
                else if (deck.SyncVersion == 0 || deck.SyncVersion != change.ExpectedVersion ||
                         deck.ArchivedAtUtc is not null)
                    throw await ConflictAsync(userId, change, deck.SyncVersion, DeckSnapshot(deck), cancellationToken);

                if (change.Operation == "archive") deck.ArchivedAtUtc = DateTimeOffset.UtcNow;
                else
                {
                    var payload = (DeckPayload)validated.Payload!;
                    deck.Name = payload.Name.Trim();
                    deck.Description = payload.Description.Trim();
                    deck.Language = payload.Language;
                    deck.TextAlignment = payload.TextAlignment;
                    deck.TypographySize = payload.TypographySize;
                }
                deck.SyncVersion = version;
                return DeckSnapshot(deck);
            }
            case "card":
            {
                var card = await database.Cards.Include(item => item.Deck)
                    .SingleOrDefaultAsync(item => item.Id == change.EntityId, cancellationToken);
                if (card is not null && card.Deck.UserId != userId)
                    throw new SyncException("Entity ID is unavailable.", new SyncConflict("card", change.EntityId, change.ExpectedVersion, 0, null, ClientPayload(change)));
                if (card is null)
                {
                    if (change.Operation != "upsert" || change.ExpectedVersion != 0)
                        throw await ConflictAsync(userId, change, 0, null, cancellationToken);
                    var incoming = (CardPayload)validated.Payload!;
                    var parent = await database.Decks.SingleOrDefaultAsync(item => item.Id == incoming.DeckId && item.UserId == userId, cancellationToken);
                    if (parent is null || parent.ArchivedAtUtc is not null)
                        throw new SyncException("Card deck is unavailable.");
                    card = new Card { Id = change.EntityId, DeckId = incoming.DeckId };
                    database.Cards.Add(card);
                }
                else if (card.SyncVersion == 0 || card.SyncVersion != change.ExpectedVersion ||
                         card.ArchivedAtUtc is not null)
                    throw await ConflictAsync(userId, change, card.SyncVersion, CardSnapshot(card), cancellationToken);

                if (change.Operation == "archive") card.ArchivedAtUtc = DateTimeOffset.UtcNow;
                else
                {
                    var payload = (CardPayload)validated.Payload!;
                    if (card.DeckId != payload.DeckId)
                        throw await ConflictAsync(userId, change, card.SyncVersion, CardSnapshot(card), cancellationToken);
                    card.FrontText = payload.FrontText.Trim();
                    card.Meaning = payload.Meaning.Trim();
                    card.Phonetic = payload.Phonetic;
                    card.Category = payload.Category;
                    card.ExamplesJson = payload.Examples.GetRawText();
                }
                card.SyncVersion = version;
                return CardSnapshot(card);
            }
            case "reviewState":
            {
                if (!await database.Cards.AnyAsync(item => item.Id == change.EntityId && item.Deck.UserId == userId, cancellationToken))
                    throw new SyncException("Review state card is unavailable.");
                var state = await database.CardReviewStates.SingleOrDefaultAsync(item => item.CardId == change.EntityId, cancellationToken);
                if (state is null)
                {
                    if (change.ExpectedVersion != 0) throw await ConflictAsync(userId, change, 0, null, cancellationToken);
                    state = new CardReviewState { CardId = change.EntityId };
                    database.CardReviewStates.Add(state);
                }
                else if (state.SyncVersion == 0 || state.SyncVersion != change.ExpectedVersion)
                    throw await ConflictAsync(userId, change, state.SyncVersion, StateSnapshot(state), cancellationToken);

                var payload = (ReviewStatePayload)validated.Payload!;
                state.Box = payload.Box;
                state.DueDate = payload.DueDate;
                state.LastReviewedAtUtc = payload.LastReviewedAtUtc;
                state.ConsecutiveSuccesses = payload.ConsecutiveSuccesses;
                state.TotalReviews = payload.TotalReviews;
                state.TotalSuccesses = payload.TotalSuccesses;
                state.SyncVersion = version;
                return StateSnapshot(state);
            }
            case "settings":
            {
                if (change.EntityId != userId) throw new SyncException("Settings ID must match the authenticated user.");
                var settings = await database.UserSettings.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
                if (settings is null)
                {
                    if (change.ExpectedVersion != 0) throw await ConflictAsync(userId, change, 0, null, cancellationToken);
                    settings = new UserSettings { UserId = userId };
                    database.UserSettings.Add(settings);
                }
                else if (settings.SyncVersion == 0 || settings.SyncVersion != change.ExpectedVersion)
                    throw await ConflictAsync(userId, change, settings.SyncVersion, SettingsSnapshot(settings), cancellationToken);

                var payload = (SettingsPayload)validated.Payload!;
                settings.Theme = payload.Theme;
                settings.HapticsEnabled = payload.HapticsEnabled;
                settings.Language = payload.Language;
                settings.DailyReminderEnabled = payload.DailyReminderEnabled;
                settings.DailyReminderTime = payload.DailyReminderTime;
                settings.PreferredSpeechLanguage = payload.PreferredSpeechLanguage;
                settings.PreferredSpeechAccent = payload.PreferredSpeechAccent;
                settings.SyncVersion = version;
                return SettingsSnapshot(settings);
            }
            case "reviewEvent":
            {
                if (await database.ReviewEvents.AnyAsync(item => item.Id == change.EntityId, cancellationToken))
                    throw await ConflictAsync(userId, change, 0, null, cancellationToken);
                var payload = (ReviewEventPayload)validated.Payload!;
                if (!await database.Cards.AnyAsync(item => item.Id == payload.CardId && item.DeckId == payload.DeckId &&
                                                     item.Deck.UserId == userId, cancellationToken))
                    throw new SyncException("Review event card and deck are unavailable.");
                database.ReviewEvents.Add(new ReviewEvent
                {
                    Id = change.EntityId, UserId = userId, DeckId = payload.DeckId, CardId = payload.CardId,
                    PreviousBox = payload.PreviousBox, NewBox = payload.NewBox, Result = payload.Result,
                    ReviewedAtUtc = payload.ReviewedAtUtc, StudySessionId = payload.StudySessionId
                });
                return JsonSerializer.Serialize(payload, SyncValidator.JsonOptions);
            }
            default:
                throw new SyncException("Unsupported entity type.");
        }
    }

    private async Task<SyncException> ConflictAsync(Guid userId, SyncPushChange change, long actualVersion, string? snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot is null && actualVersion > 0)
            snapshot = await database.SyncChanges.AsNoTracking().Where(item =>
                    item.UserId == userId && item.EntityType == change.EntityType && item.EntityId == change.EntityId &&
                    item.Version == actualVersion)
                .Select(item => item.PayloadJson).SingleOrDefaultAsync(cancellationToken);
        return new SyncException("Change conflicts with the server version.",
            new SyncConflict(change.EntityType, change.EntityId, change.ExpectedVersion, actualVersion,
                snapshot is null ? null : ParseJson(snapshot), ClientPayload(change)));
    }

    private static JsonElement? ClientPayload(SyncPushChange change) =>
        change.Payload.ValueKind == JsonValueKind.Object ? change.Payload.Clone() : null;

    private static JsonElement ParseJson(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string DeckSnapshot(Deck deck) => JsonSerializer.Serialize(new
    {
        deck.Name, deck.Description, deck.Language, deck.TextAlignment, deck.TypographySize,
        deck.ArchivedAtUtc
    }, SyncValidator.JsonOptions);

    private static string CardSnapshot(Card card) => JsonSerializer.Serialize(new
    {
        card.DeckId, card.FrontText, card.Meaning, card.Phonetic, card.Category,
        Examples = ParseJson(card.ExamplesJson), card.ArchivedAtUtc
    }, SyncValidator.JsonOptions);

    private static string StateSnapshot(CardReviewState state) => JsonSerializer.Serialize(new
    {
        state.Box, state.DueDate, state.LastReviewedAtUtc, state.ConsecutiveSuccesses,
        state.TotalReviews, state.TotalSuccesses
    }, SyncValidator.JsonOptions);

    private static string SettingsSnapshot(UserSettings settings) => JsonSerializer.Serialize(new
    {
        settings.Theme, settings.HapticsEnabled, settings.Language, settings.DailyReminderEnabled,
        settings.DailyReminderTime, settings.PreferredSpeechLanguage, settings.PreferredSpeechAccent
    }, SyncValidator.JsonOptions);
}
