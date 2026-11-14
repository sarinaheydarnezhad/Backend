using System.Text.Json;
using System.Text.Json.Serialization;

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

public sealed record SyncPullResponse(long Cursor, long LatestVersion, bool HasMore, IReadOnlyList<SyncPullChange> Changes,
    IReadOnlyList<ReviewCheckpoint>? ReviewCheckpoints = null, int SchedulingContractVersion = 2);

// Captured under the same per-user head lock as ThroughVersion; it covers every earlier server event.
public sealed record ReviewCheckpoint(Guid CardId, long ThroughVersion, ReviewStatePayload State);
public sealed record ReviewCalendarPayload(DateOnly ReviewDate, int UtcOffsetMinutes);

public sealed record SyncConflict(
    string EntityType, Guid EntityId, long ExpectedVersion, long ActualVersion,
    JsonElement? ServerValue, JsonElement? ClientValue);

public sealed class SyncException(string message, SyncConflict? conflict = null, string? code = null,
    int? statusCode = null) : Exception(message)
{
    public SyncConflict? Conflict { get; } = conflict;
    public string? Code { get; } = code;
    public int StatusCode { get; } = statusCode ?? (conflict is null ? 400 : 409);
}

public sealed record DeckPayload(string Name, string Description, string Language, string TextAlignment, string TypographySize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewSystem = null);

public sealed record CardPayload(Guid DeckId, string FrontText, string Meaning, string? Phonetic, string? Category, JsonElement Examples);

public sealed record ReviewStatePayload(int Box, DateOnly DueDate, DateTimeOffset? LastReviewedAtUtc,
    int ConsecutiveSuccesses, int TotalReviews, int TotalSuccesses,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SchedulerStatePayload? SchedulerState = null);

public sealed record ReviewEventPayload(Guid DeckId, Guid CardId, int PreviousBox, int NewBox,
    string Result, DateTimeOffset ReviewedAtUtc, Guid? StudySessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SchedulerEventPayload? Scheduler = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewCalendarPayload? ReviewCalendar = null);

public sealed record SettingsPayload(string Theme, bool HapticsEnabled, string Language,
    bool DailyReminderEnabled, TimeOnly? DailyReminderTime, string PreferredSpeechLanguage, string? PreferredSpeechAccent);
