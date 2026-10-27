using Microsoft.AspNetCore.DataProtection;

namespace Flashcards.Infrastructure.Backups;

public interface IBackupPayloadProtector
{
    byte[] Protect(Guid userId, Guid backupId, int formatVersion, byte[] payload);
    byte[] Unprotect(Guid userId, Guid backupId, int formatVersion, byte[] payload);
}

public sealed class BackupPayloadProtector(IDataProtectionProvider provider) : IBackupPayloadProtector
{
    public byte[] Protect(Guid userId, Guid backupId, int formatVersion, byte[] payload) =>
        provider.CreateProtector("Lerona.Backups", formatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            userId.ToString("D"), backupId.ToString("D")).Protect(payload);

    public byte[] Unprotect(Guid userId, Guid backupId, int formatVersion, byte[] payload) =>
        provider.CreateProtector("Lerona.Backups", formatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            userId.ToString("D"), backupId.ToString("D")).Unprotect(payload);
}
