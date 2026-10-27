using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Flashcards.Infrastructure.Backups;

internal sealed record BackupDocument(int FormatVersion, Guid BackupId, Guid UserId, DateTimeOffset CapturedAtUtc,
    long SyncVersion, List<BackupDeck> Decks, List<BackupCard> Cards, List<BackupReviewState> ReviewStates,
    List<BackupReviewEvent> ReviewEvents, List<BackupUserSettings> Settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    internal static BackupDocument Read(byte[] payload, Guid userId, Guid backupId, long syncVersion)
    {
        try
        {
            using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateProperties(json.RootElement);
            var document = json.RootElement.Deserialize<BackupDocument>(JsonOptions);
            Require(document is not null);
            BackupSnapshot.ValidateVersion(document!.FormatVersion);
            Require(document.BackupId == backupId && document.UserId == userId && document.SyncVersion == syncVersion &&
                document.SyncVersion >= 0 && document.CapturedAtUtc != default);
            Require(document.Decks is not null && document.Cards is not null && document.ReviewStates is not null &&
                document.ReviewEvents is not null && document.Settings is { Count: <= 1 });
            Require(document.Decks!.All(item => item is not null) && document.Cards!.All(item => item is not null) &&
                document.ReviewStates!.All(item => item is not null) && document.ReviewEvents!.All(item => item is not null) &&
                document.Settings!.All(item => item is not null));
            Require(Unique(document.Decks.Select(item => item.Id)) && Unique(document.Cards.Select(item => item.Id)) &&
                Unique(document.ReviewStates.Select(item => item.CardId)) && Unique(document.ReviewEvents.Select(item => item.Id)));
            var decks = document.Decks.ToDictionary(item => item.Id);
            var cards = document.Cards.ToDictionary(item => item.Id);
            var states = document.ReviewStates.ToDictionary(item => item.CardId);
            foreach (var deck in document.Decks)
            {
                Require(Text(deck.Name, 120, true) && Text(deck.Description, 1000) && Text(deck.Language, 35, true) &&
                    deck.TextAlignment is "ltr" or "rtl" or "center" && deck.TypographySize is "small" or "medium" or "large");
                document.ValidateDates(deck.CreatedAtUtc, deck.UpdatedAtUtc, deck.ArchivedAtUtc, deck.SyncVersion);
            }
            foreach (var card in document.Cards)
            {
                Require(decks.ContainsKey(card.DeckId) && Text(card.FrontText, 4000, true) && Text(card.Meaning, 4000, true) &&
                    (card.Phonetic is null || Text(card.Phonetic, 500)) && (card.Category is null || Text(card.Category, 120)) &&
                    card.ExamplesJson is not null);
                using var examples = JsonDocument.Parse(card.ExamplesJson!);
                Require(examples.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object);
                document.ValidateDates(card.CreatedAtUtc, card.UpdatedAtUtc, card.ArchivedAtUtc, card.SyncVersion);
            }
            foreach (var state in document.ReviewStates)
            {
                Require(cards.ContainsKey(state.CardId) && state.Box is >= 1 and <= 5 && state.DueDate != default &&
                    state.TotalReviews >= 0 && state.TotalSuccesses >= 0 && state.TotalSuccesses <= state.TotalReviews &&
                    state.ConsecutiveSuccesses >= 0 && state.ConsecutiveSuccesses <= state.TotalSuccesses && state.LastReviewedAtUtc != default(DateTimeOffset));
                document.ValidateDates(state.CreatedAtUtc, state.UpdatedAtUtc, null, state.SyncVersion);
            }
            foreach (var review in document.ReviewEvents)
                Require(cards.TryGetValue(review.CardId, out var card) && card.DeckId == review.DeckId &&
                    review.PreviousBox is >= 1 and <= 5 && review.NewBox is >= 1 and <= 5 &&
                    review.Result is "success" or "failure" && review.ReviewedAtUtc != default &&
                    review.CreatedAtUtc != default && review.StudySessionId != Guid.Empty && states.ContainsKey(review.CardId) &&
                    (review.ServerVersion is null || review.ServerVersion > 0 && review.ServerVersion <= document.SyncVersion));
            foreach (var settings in document.Settings)
            {
                Require(settings.Theme is "system" or "light" or "dark" or "oled" && Text(settings.Language, 35, true) &&
                    Text(settings.PreferredSpeechLanguage, 35, true) && settings.PreferredSpeechAccent is null or "us" or "uk" &&
                    (!settings.DailyReminderEnabled || settings.DailyReminderTime is not null));
                document.ValidateDates(settings.CreatedAtUtc, settings.UpdatedAtUtc, null, settings.SyncVersion);
            }
            return document;
        }
        catch (JsonException) { throw Invalid(); }
    }

    private void ValidateDates(DateTimeOffset created, DateTimeOffset updated, DateTimeOffset? archived, long version) =>
        Require(created != default && updated >= created && archived != default(DateTimeOffset) && version >= 0 && version <= SyncVersion);

    private static bool Text(string? value, int maximum, bool nonempty = false) =>
        value is not null && value.Length <= maximum && (!nonempty || !string.IsNullOrWhiteSpace(value));

    private static bool Unique(IEnumerable<Guid> identifiers)
    {
        var seen = new HashSet<Guid>();
        return identifiers.All(identifier => identifier != Guid.Empty && seen.Add(identifier));
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                Require(names.Add(property.Name));
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void Require([DoesNotReturnIf(false)] bool valid)
    {
        if (!valid) throw Invalid();
    }

    private static BackupException Invalid() => new(400, "backup_invalid", "The backup is incomplete, corrupted or contains invalid relationships.");
}

internal sealed record BackupDeck(Guid Id, string Name, string Description, string Language, string TextAlignment,
    string TypographySize, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? ArchivedAtUtc, long SyncVersion);
internal sealed record BackupCard(Guid Id, Guid DeckId, string FrontText, string? Phonetic, string? Category,
    string Meaning, string ExamplesJson, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? ArchivedAtUtc, long SyncVersion);
internal sealed record BackupReviewState(Guid CardId, int Box, DateOnly DueDate, DateTimeOffset? LastReviewedAtUtc,
    int ConsecutiveSuccesses, int TotalReviews, int TotalSuccesses, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long SyncVersion);
internal sealed record BackupReviewEvent(Guid Id, Guid DeckId, Guid CardId, int PreviousBox, int NewBox, string Result,
    DateTimeOffset ReviewedAtUtc, Guid? StudySessionId, DateTimeOffset CreatedAtUtc, long? ServerVersion = null);
internal sealed record BackupUserSettings(string Theme, bool HapticsEnabled, string Language, bool DailyReminderEnabled,
    TimeOnly? DailyReminderTime, string PreferredSpeechLanguage, string? PreferredSpeechAccent,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long SyncVersion);
