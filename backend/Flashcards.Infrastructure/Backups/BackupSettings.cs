using Microsoft.Extensions.Configuration;

namespace Flashcards.Infrastructure.Backups;

public sealed record BackupSettings(int RetentionDays, int MaxPayloadBytes, int MaxStoredBackupsPerUser, string? KeyRingPath)
{
    public static BackupSettings FromConfiguration(IConfiguration configuration) => new(
        configuration.GetValue<int>("Backups:RetentionDays"),
        configuration.GetValue<int>("Backups:MaxPayloadBytes"),
        configuration.GetValue<int>("Backups:MaxStoredBackupsPerUser"),
        configuration["Backups:KeyRingPath"]);

    public bool IsConfigured => RetentionDays is >= 1 and <= 3650 &&
        MaxPayloadBytes is >= 1024 and <= 104857600 && MaxStoredBackupsPerUser is >= 1 and <= 1000 &&
        !string.IsNullOrWhiteSpace(KeyRingPath) && Path.IsPathFullyQualified(KeyRingPath);
}
