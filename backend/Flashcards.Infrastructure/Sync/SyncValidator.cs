using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flashcards.Infrastructure.Sync;

internal sealed record ValidatedSyncChange(SyncPushChange Change, object? Payload, string Fingerprint);

internal static class SyncValidator
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IReadOnlyList<ValidatedSyncChange> Validate(SyncPushRequest? request)
    {
        if (request?.Changes is not { Count: > 0 and <= 50 })
            throw new SyncException("A push requires between 1 and 50 changes.");

        var seen = new HashSet<(Guid DeviceId, Guid ClientChangeId)>();
        var validated = new List<ValidatedSyncChange>(request.Changes.Count);
        foreach (var change in request.Changes)
        {
            if (change is null || change.EntityId == Guid.Empty || change.DeviceId == Guid.Empty ||
                change.ClientChangeId == Guid.Empty || change.ClientChangedAtUtc == default ||
                change.ExpectedVersion < 0 || !seen.Add((change.DeviceId, change.ClientChangeId)))
                throw new SyncException("Invalid change identity or version.");

            var type = change.EntityType;
            var operation = change.Operation;
            var allowed = (type, operation) switch
            {
                ("deck" or "card", "upsert" or "archive") => true,
                ("reviewState" or "settings", "upsert") => true,
                ("reviewEvent", "create") => true,
                _ => false
            };
            if (!allowed || (type == "reviewEvent" && change.ExpectedVersion != 0))
                throw new SyncException("Unsupported entity type, operation, or base version.");

            object? payload;
            if (operation == "archive")
            {
                if (change.Payload.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    throw new SyncException("Archive operations cannot contain a payload.");
                payload = null;
            }
            else
            {
                if (change.Payload.ValueKind != JsonValueKind.Object || change.Payload.GetRawText().Length > 16_384)
                    throw new SyncException("Change payload must be a bounded JSON object.");

                try
                {
                    payload = type switch
                    {
                        "deck" => change.Payload.Deserialize<DeckPayload>(JsonOptions),
                        "card" => change.Payload.Deserialize<CardPayload>(JsonOptions),
                        "reviewState" => change.Payload.Deserialize<ReviewStatePayload>(JsonOptions),
                        "reviewEvent" => change.Payload.Deserialize<ReviewEventPayload>(JsonOptions),
                        "settings" => change.Payload.Deserialize<SettingsPayload>(JsonOptions),
                        _ => null
                    };
                }
                catch (JsonException) { throw new SyncException("Invalid change payload."); }
                if (payload is null) throw new SyncException("Invalid change payload.");
                ValidatePayload(payload);
            }

            var canonical = JsonSerializer.Serialize(new
            {
                type, change.EntityId, operation, change.DeviceId, change.ClientChangeId,
                ClientChangedAtUtc = change.ClientChangedAtUtc.ToUniversalTime(),
                change.ExpectedVersion, payload
            }, JsonOptions);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            validated.Add(new ValidatedSyncChange(change, payload, fingerprint));
        }
        return validated;
    }

    private static void ValidatePayload(object payload)
    {
        switch (payload)
        {
            case DeckPayload deck:
                if (string.IsNullOrWhiteSpace(deck.Name) || deck.Name.Length > 120 ||
                    deck.Description is null || deck.Description.Length > 1000 || !LanguageIsValid(deck.Language) ||
                    deck.TextAlignment is not ("ltr" or "rtl" or "center") ||
                    deck.TypographySize is not ("small" or "medium" or "large"))
                    throw new SyncException("Invalid deck payload.");
                break;
            case CardPayload card:
                if (card.DeckId == Guid.Empty || string.IsNullOrWhiteSpace(card.FrontText) || card.FrontText.Length > 4000 ||
                    string.IsNullOrWhiteSpace(card.Meaning) || card.Meaning.Length > 4000 ||
                    card.Phonetic?.Length > 500 || card.Category?.Length > 120 ||
                    card.Examples.ValueKind != JsonValueKind.Array || card.Examples.GetArrayLength() > 20)
                    throw new SyncException("Invalid card payload.");
                foreach (var example in card.Examples.EnumerateArray())
                {
                    if (example.ValueKind != JsonValueKind.Object || !example.TryGetProperty("sentence", out var sentence) ||
                        sentence.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(sentence.GetString()) ||
                        sentence.GetString()!.Length > 4000)
                        throw new SyncException("Invalid card example.");
                }
                break;
            case ReviewStatePayload state:
                if (state.Box is < 1 or > 5 || state.DueDate == default || state.ConsecutiveSuccesses < 0 ||
                    state.TotalReviews < 0 || state.TotalSuccesses < 0 || state.TotalSuccesses > state.TotalReviews ||
                    state.ConsecutiveSuccesses > state.TotalSuccesses)
                    throw new SyncException("Invalid review state payload.");
                break;
            case ReviewEventPayload review:
                if (review.DeckId == Guid.Empty || review.CardId == Guid.Empty ||
                    review.PreviousBox is < 1 or > 5 || review.NewBox is < 1 or > 5 ||
                    review.Result is not ("success" or "failure") || review.ReviewedAtUtc == default ||
                    review.StudySessionId == Guid.Empty)
                    throw new SyncException("Invalid review event payload.");
                break;
            case SettingsPayload settings:
                if (settings.Theme is not ("system" or "light" or "dark" or "oled") ||
                    !LanguageIsValid(settings.Language) || !LanguageIsValid(settings.PreferredSpeechLanguage) ||
                    (settings.DailyReminderEnabled && settings.DailyReminderTime is null) ||
                    settings.PreferredSpeechAccent is not (null or "us" or "uk") ||
                    (settings.PreferredSpeechAccent is not null &&
                     !settings.PreferredSpeechLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase)))
                    throw new SyncException("Invalid settings payload.");
                break;
        }
    }

    private static bool LanguageIsValid(string? language) =>
        !string.IsNullOrWhiteSpace(language) && language.Length <= 35 &&
        language.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
