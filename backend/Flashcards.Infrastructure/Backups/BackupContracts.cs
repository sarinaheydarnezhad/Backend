namespace Flashcards.Infrastructure.Backups;

public sealed record CreateBackupRequest(Guid RequestId, int FormatVersion, long ExpectedSyncVersion);
public sealed record BackupMetadata(Guid Id, string Status, int FormatVersion, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc, DateTimeOffset ExpiresAtUtc, int RetentionDays, long? SyncVersion,
    long? SizeBytes, string? FailureCode, bool IsExpired, bool CanRestore);
public sealed record BackupCreation(BackupMetadata Backup, bool Replayed);
public sealed record BackupPage(IReadOnlyList<BackupMetadata> Items, int? NextPage);
public sealed record BackupStatus(BackupMetadata? LatestCompleted, int RetentionDays, int MaxPayloadBytes,
    bool RestoreSupported = false, bool AutomaticCleanup = false);
public sealed record RestoreBackupRequest(string Mode, long ExpectedSyncVersion);
public sealed record BackupRestoreResult(Guid BackupId, string Mode, long Cursor, int Decks, int Cards,
    int ReviewStates, int ReviewEvents, int Settings, int SkippedDecks, int SkippedCards, int SkippedSettings);

public sealed class BackupException(int statusCode, string code, string message, Guid? backupId = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public Guid? BackupId { get; } = backupId;
}
