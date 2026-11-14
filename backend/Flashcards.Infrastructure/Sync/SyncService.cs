using System.Data;
using System.Text.Json;
using System.Text;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Sync;

public sealed class SyncService(FlashcardsDbContext database, EntitlementService entitlements)
{
    public async Task<Deck> CreateDeckAsync(Guid userId, string name, CancellationToken cancellationToken = default)
    {
        var deck = new Deck { Name = name };
        var payload = new DeckPayload(deck.Name, deck.Description, deck.Language, deck.TextAlignment, deck.TypographySize);
        var change = new SyncPushChange(Guid.NewGuid(), Guid.NewGuid(), "deck", deck.Id, "upsert",
            DateTimeOffset.UtcNow, 0, JsonSerializer.SerializeToElement(payload, SyncValidator.JsonOptions));

        await PushAsync(userId, new SyncPushRequest([change]), cancellationToken);
        return database.Decks.Local.Single(item => item.Id == deck.Id);
    }

    public Task<SyncPushResponse> PushAsync(Guid userId, SyncPushRequest request, CancellationToken cancellationToken = default) =>
        PushValidatedAsync(userId, SyncValidator.Validate(request), cancellationToken);

    public Task<SyncPushResponse> MigrateGuestAsync(Guid userId, SyncPushRequest request, CancellationToken cancellationToken = default) =>
        PushValidatedAsync(userId, GuestMigrationValidator.Validate(request), cancellationToken);

    private async Task<SyncPushResponse> PushValidatedAsync(Guid userId, IReadOnlyList<ValidatedSyncChange> changes,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ApplyValidatedAsync(userId, changes, cancellationToken);
        }
        catch
        {
            database.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<SyncPushResponse> ApplyValidatedAsync(Guid userId, IReadOnlyList<ValidatedSyncChange> changes,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await SyncBootstrap.LockHeadAsync(database, userId, cancellationToken);
        var version = await database.SyncHeads.AsNoTracking()
            .Where(item => item.UserId == userId).Select(item => item.Version).SingleAsync(cancellationToken);
        if (version == 0) version = await SyncBootstrap.InitializeAsync(database, userId, cancellationToken);
        var acknowledgements = new List<SyncPushAcknowledgement>(changes.Count);

        foreach (var validated in changes)
        {
            var change = validated.Change;
            var receipt = await database.SyncChanges.AsNoTracking().SingleOrDefaultAsync(item =>
                item.UserId == userId && item.DeviceId == change.DeviceId && item.ClientChangeId == change.ClientChangeId,
                cancellationToken);
            if (receipt is not null)
            {
                if (receipt.Fingerprint != validated.Fingerprint && !IsLegacyLeitnerRetry(receipt, validated))
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
        try
        {
            return await PullCoreAsync(userId, cursor, limit, cancellationToken);
        }
        catch
        {
            database.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<SyncPullResponse> PullCoreAsync(Guid userId, long cursor, int limit, CancellationToken cancellationToken)
    {
        if (cursor < 0 || limit is < 1 or > 100) throw new SyncException("Invalid cursor or page size.");
        // A mutable projection is a checkpoint ONLY with a watermark captured under this same lock.
        await using var readTransaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await SyncBootstrap.LockHeadAsync(database, userId, cancellationToken);
        var head = await database.SyncHeads.AsNoTracking().Where(item => item.UserId == userId).Select(item => new
        {
            item.Version,
            HasLegacyData = item.Version == 0 && (database.Decks.Any(deck => deck.UserId == userId) ||
                database.UserSettings.Any(settings => settings.UserId == userId))
        }).SingleOrDefaultAsync(cancellationToken);
        var latest = head?.Version ?? 0;
        if (head is null || head.HasLegacyData)
        {
            latest = await SyncBootstrap.InitializeAsync(database, userId, cancellationToken);
        }
        if (cursor > latest) throw new SyncException("Cursor is ahead of the server version.");
        if (cursor == latest)
        {
            await readTransaction.CommitAsync(cancellationToken);
            return new SyncPullResponse(cursor, latest, false, [], []);
        }

        var page = new List<SyncChange>();
        long pageBytes = 1024;
        await foreach (var item in database.SyncChanges.AsNoTracking()
            .Where(item => item.UserId == userId && item.Version > cursor && item.Version <= latest)
            .OrderBy(item => item.Version).Take(limit).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            // Reserve envelope + checkpoint space per entry. Stream rather than materializing 100 maximum cards.
            var bytes = Encoding.UTF8.GetByteCount(item.PayloadJson) + 2048L;
            if (page.Count > 0 && pageBytes + bytes > CardContentContract.PushBytes) break;
            if (pageBytes + bytes > CardContentContract.PushBytes)
                throw new SyncException($"Synchronized record {item.Version} exceeds the transport contract. Original data was retained for controlled recovery.",
                    code: "sync_payload_recovery_required", statusCode: 409);
            page.Add(item);
            pageBytes += bytes;
        }
        if (page.Count == 0 || page[0].Version != cursor + 1 ||
            page.Where((item, index) => item.Version != cursor + index + 1).Any())
            throw new SyncException("The synchronized history is incomplete. Contact support; local data must be retained.",
                code: "sync_history_incomplete", statusCode: 409);
        var nextCursor = page.Count == 0 ? cursor : page[^1].Version;
        var changes = page.Select(item => new SyncPullChange(
            item.EntityType, item.EntityId, item.Operation, item.DeviceId, item.ClientChangeId,
            item.ClientChangedAtUtc, item.ServerChangedAtUtc, item.ExpectedVersion, item.Version, ParseJson(item.PayloadJson))).ToList();
        var cardIds = changes.Where(item => item.EntityType is "card" or "reviewState" or "reviewEvent")
            .Select(item => item.EntityType == "reviewEvent" ? item.Payload.GetProperty("cardId").GetGuid() : item.EntityId).Distinct().ToArray();
        var states = await database.CardReviewStates.AsNoTracking()
            .Where(item => cardIds.Contains(item.CardId) && item.Card.Deck.UserId == userId).ToListAsync(cancellationToken);
        var checkpoints = states.Select(item => new ReviewCheckpoint(item.CardId, latest, new ReviewStatePayload(
            item.Box, item.DueDate, item.LastReviewedAtUtc, item.ConsecutiveSuccesses, item.TotalReviews, item.TotalSuccesses,
            SchedulerSync.Read<SchedulerStatePayload>(item.SchedulerStateJson)))).ToList();
        await readTransaction.CommitAsync(cancellationToken);
        return new SyncPullResponse(nextCursor, latest, nextCursor < latest, changes, checkpoints);
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
                    await entitlements.EnsureDeckCreationAllowedAsync(userId, cancellationToken);
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
                    if (payload.ReviewSystem is not null && deck.ReviewSystem != payload.ReviewSystem &&
                        (await database.Cards.AnyAsync(item => item.DeckId == deck.Id, cancellationToken) ||
                         await database.ReviewEvents.AnyAsync(item => item.DeckId == deck.Id, cancellationToken)))
                        throw await ConflictAsync(userId, change, deck.SyncVersion, DeckSnapshot(deck), cancellationToken);
                    deck.Name = payload.Name.Trim();
                    deck.Description = payload.Description.Trim();
                    deck.Language = payload.Language;
                    deck.TextAlignment = payload.TextAlignment;
                    deck.TypographySize = payload.TypographySize;
                    deck.ReviewSystem = payload.ReviewSystem ?? deck.ReviewSystem;
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
                    await entitlements.EnsureCardCreationAllowedAsync(userId, cancellationToken);
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
                    card.FrontText = CardContentContract.Trim(payload.FrontText);
                    card.Meaning = CardContentContract.Trim(payload.Meaning);
                    card.Phonetic = payload.Phonetic;
                    card.Category = payload.Category;
                    card.ExamplesJson = payload.Examples.GetRawText();
                }
                card.SyncVersion = version;
                return CardSnapshot(card);
            }
            case "reviewState":
            {
                if (!await database.Cards.AnyAsync(item => item.Id == change.EntityId && item.Deck.UserId == userId &&
                    item.ArchivedAtUtc == null && item.Deck.ArchivedAtUtc == null, cancellationToken))
                    throw new SyncException("Review state card is unavailable.");
                await entitlements.EnsureStudyAllowedAsync(userId, cancellationToken);
                if (await database.ReviewEvents.AnyAsync(item => item.CardId == change.EntityId && item.UserId == userId, cancellationToken))
                    throw new SyncException("Review state is derived from immutable review events.");
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
                if (state.TotalReviews > 0 && payload != new ReviewStatePayload(state.Box, state.DueDate,
                    state.LastReviewedAtUtc, state.ConsecutiveSuccesses, state.TotalReviews, state.TotalSuccesses,
                    SchedulerSync.Read<SchedulerStatePayload>(state.SchedulerStateJson)))
                    throw await ConflictAsync(userId, change, state.SyncVersion, StateSnapshot(state), cancellationToken);
                SchedulerSync.ValidateNewTime(payload.LastReviewedAtUtc, DateTimeOffset.UtcNow);
                var reviewSystem = await database.Cards.Where(item => item.Id == change.EntityId)
                    .Select(item => item.Deck.ReviewSystem).SingleAsync(cancellationToken);
                if ((payload.SchedulerState?.SchedulerId ?? "leitner") != reviewSystem)
                    throw new SyncException("Review state does not match the deck review system.");
                state.SchedulerStateJson = SchedulerSync.Json(payload.SchedulerState);
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
                SchedulerSync.ValidateNewTime(payload.ReviewedAtUtc, DateTimeOffset.UtcNow);
                if (!await database.Cards.AnyAsync(item => item.Id == payload.CardId && item.DeckId == payload.DeckId &&
                                                     item.Deck.UserId == userId && item.ArchivedAtUtc == null && item.Deck.ArchivedAtUtc == null, cancellationToken))
                    throw new SyncException("Review event card and deck are unavailable.");
                await entitlements.EnsureStudyAllowedAsync(userId, cancellationToken);
                var reviewSystem = await database.Decks.Where(item => item.Id == payload.DeckId)
                    .Select(item => item.ReviewSystem).SingleAsync(cancellationToken);
                if ((payload.Scheduler?.SchedulerId ?? "leitner") != reviewSystem)
                    throw new SyncException("Review event does not match the deck review system.");
                database.ReviewEvents.Add(new ReviewEvent
                {
                    Id = change.EntityId, UserId = userId, DeckId = payload.DeckId, CardId = payload.CardId,
                    PreviousBox = payload.PreviousBox, NewBox = payload.NewBox, Result = payload.Result,
                    SchedulerJson = SchedulerSync.Json(payload.Scheduler),
                    ReviewCalendarJson = SchedulerSync.Json(payload.ReviewCalendar),
                    ReviewedAtUtc = payload.ReviewedAtUtc, StudySessionId = payload.StudySessionId
                });
                var state = await database.CardReviewStates.SingleOrDefaultAsync(item => item.CardId == payload.CardId, cancellationToken);
                if (state is null)
                {
                    state = new CardReviewState { CardId = payload.CardId };
                    database.CardReviewStates.Add(state);
                }
                SchedulerSync.Apply(state, payload.Result, payload.ReviewedAtUtc, reviewSystem, payload.ReviewCalendar);
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

    private static bool IsLegacyLeitnerRetry(SyncChange receipt, ValidatedSyncChange validated)
    {
        if (validated.Payload is not DeckPayload { ReviewSystem: "leitner" } payload ||
            validated.Change.Operation != "upsert" || receipt.EntityType != "deck" || receipt.Operation != "upsert" ||
            receipt.EntityId != validated.Change.EntityId) return false;
        // Old CloudClient omitted explicit Leitner. Reconstruct that exact request fingerprint;
        // no other payload/header differences are permitted and no receipt is rewritten.
        var legacy = validated.Change with
        {
            Payload = JsonSerializer.SerializeToElement(payload with { ReviewSystem = null }, SyncValidator.JsonOptions)
        };
        if (SyncValidator.Validate(new SyncPushRequest([legacy]))[0].Fingerprint != receipt.Fingerprint) return false;
        using var snapshot = JsonDocument.Parse(receipt.PayloadJson);
        // Omission on an SM-2 deck meant "keep SM-2", so it is NOT equivalent to selecting Leitner.
        return snapshot.RootElement.TryGetProperty("reviewSystem", out var system) &&
            system.ValueKind == JsonValueKind.String && system.GetString() == "leitner";
    }

    private static JsonElement ParseJson(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    internal static string DeckSnapshot(Deck deck) => JsonSerializer.Serialize(new
    {
        deck.Name, deck.Description, deck.Language, deck.TextAlignment, deck.TypographySize, deck.ReviewSystem,
        deck.ArchivedAtUtc
    }, SyncValidator.JsonOptions);

    internal static string CardSnapshot(Card card)
    {
        JsonElement examples;
        try
        {
            examples = ParseJson(card.ExamplesJson);
            CardContentContract.Validate(new CardPayload(card.DeckId, card.FrontText, card.Meaning, card.Phonetic, card.Category, examples));
        }
        catch (Exception error) when (error is JsonException or SyncException)
        {
            throw new SyncException($"Card integrity error for {card.Id}. Original content and learning history require controlled recovery.", code: "card_integrity_error", statusCode: 409);
        }
        return JsonSerializer.Serialize(new
        {
            card.DeckId, card.FrontText, card.Meaning, card.Phonetic, card.Category,
            Examples = examples, card.ArchivedAtUtc
        }, SyncValidator.JsonOptions);
    }

    internal static string StateSnapshot(CardReviewState state) => JsonSerializer.Serialize(new
    {
        state.Box, state.DueDate, state.LastReviewedAtUtc, state.ConsecutiveSuccesses,
        state.TotalReviews, state.TotalSuccesses,
        SchedulerState = SchedulerSync.Read<SchedulerStatePayload>(state.SchedulerStateJson)
    }, SyncValidator.JsonOptions);

    internal static string SettingsSnapshot(UserSettings settings) => JsonSerializer.Serialize(new
    {
        settings.Theme, settings.HapticsEnabled, settings.Language, settings.DailyReminderEnabled,
        settings.DailyReminderTime, settings.PreferredSpeechLanguage, settings.PreferredSpeechAccent
    }, SyncValidator.JsonOptions);
}
