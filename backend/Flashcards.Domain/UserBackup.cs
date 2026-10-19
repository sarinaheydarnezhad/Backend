namespace Flashcards.Domain;

public sealed class UserBackup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid RequestId { get; set; }
    public string Status { get; set; } = "Pending";
    public int FormatVersion { get; set; }
    public long RequestedSyncVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int RetentionDays { get; set; }
    public long? SyncVersion { get; set; }
    public long? SizeBytes { get; set; }
    public byte[]? Payload { get; set; }
    public string? FailureCode { get; set; }
}
