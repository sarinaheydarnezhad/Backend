using System.Text.Json;

namespace Flashcards.Infrastructure.Sync;

public sealed record SyncPushRequest(IReadOnlyList<SyncPushChange> Changes);

public sealed record SyncPushChange(
    Guid ClientChangeId,
    Guid DeviceId,
    string EntityType,
    Guid EntityId,
    string Operation,
    DateTimeOffset ClientChangedAtUtc,
    long ExpectedVersion,
    JsonElement Payload);

public sealed record SyncPushAcknowledgement(Guid ClientChangeId, string Status, long ServerVersion);

public sealed record SyncPushResponse(long Cursor, IReadOnlyList<SyncPushAcknowledgement> Changes);

public sealed record SyncPullChange(
    string EntityType,
    Guid EntityId,
    string Operation,
    Guid DeviceId,
    Guid ClientChangeId,
    DateTimeOffset ClientChangedAtUtc,
    DateTimeOffset ServerChangedAtUtc,
    long ExpectedVersion,
    long ServerVersion,
    JsonElement Payload);

public sealed record SyncPullResponse(long Cursor, long LatestVersion, bool HasMore, IReadOnlyList<SyncPullChange> Changes);

public sealed record SyncConflict(
    string EntityType, Guid EntityId, long ExpectedVersion, long ActualVersion,
    JsonElement? ServerValue, JsonElement? ClientValue);

public sealed class SyncException(string message, SyncConflict? conflict = null) : Exception(message)
{
    public SyncConflict? Conflict { get; } = conflict;
}

public sealed record DeckPayload(string Name, string Description, string Language, string TextAlignment, string TypographySize);

public sealed record CardPayload(Guid DeckId, string FrontText, string Meaning, string? Phonetic, string? Category, JsonElement Examples);

public sealed record ReviewStatePayload(int Box, DateOnly DueDate, DateTimeOffset? LastReviewedAtUtc,
    int ConsecutiveSuccesses, int TotalReviews, int TotalSuccesses);

public sealed record ReviewEventPayload(Guid DeckId, Guid CardId, int PreviousBox, int NewBox,
    string Result, DateTimeOffset ReviewedAtUtc, Guid? StudySessionId);

public sealed record SettingsPayload(string Theme, bool HapticsEnabled, string Language,
    bool DailyReminderEnabled, TimeOnly? DailyReminderTime, string PreferredSpeechLanguage, string? PreferredSpeechAccent);
