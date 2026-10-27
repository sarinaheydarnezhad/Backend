using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Flashcards.Infrastructure.Sync;

namespace Flashcards.Infrastructure.Backups;

public sealed class BackupService(FlashcardsDbContext database, BackupSettings settings,
    IBackupPayloadProtector protector, TimeProvider clock, ILogger<BackupService> logger)
{
    public async Task<BackupCreation> CreateAsync(Guid userId, CreateBackupRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        BackupSnapshot.ValidateVersion(request.FormatVersion);
        if (request.RequestId == Guid.Empty || request.ExpectedSyncVersion < 0)
            throw new BackupException(400, "invalid_backup_request", "Provide a request UUID and a non-negative sync version.");

        UserBackup? attempted = null;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await SyncBootstrap.LockHeadAsync(database, userId, cancellationToken);
            var backup = await database.UserBackups.SingleOrDefaultAsync(item => item.UserId == userId && item.RequestId == request.RequestId, cancellationToken);
            if (backup is not null)
            {
                if (backup.RequestedSyncVersion != request.ExpectedSyncVersion || backup.FormatVersion != request.FormatVersion)
                    throw new BackupException(409, "backup_request_conflict", "This request ID was already used for a different backup request.");
                if (backup.ExpiresAtUtc <= clock.GetUtcNow())
                    throw new BackupException(410, "backup_expired", "This backup request has expired. Use a new request ID.");
                if (backup.Status == "Completed")
                    return new BackupCreation(ToMetadata(backup), true);
            }
            else
            {
                if (await database.UserBackups.CountAsync(item => item.UserId == userId, cancellationToken) >= settings.MaxStoredBackupsPerUser)
                    throw new BackupException(409, "backup_capacity_reached", "Stored backup capacity is reached. Contact support for expired-artifact cleanup.");
                backup = new UserBackup
                {
                    UserId = userId, RequestId = request.RequestId, FormatVersion = request.FormatVersion,
                    RequestedSyncVersion = request.ExpectedSyncVersion, CreatedAtUtc = clock.GetUtcNow()
                };
                database.UserBackups.Add(backup);
            }

            attempted = backup;
            backup.Status = "Pending";
            backup.FailureCode = null;
            backup.Payload = null;
            backup.SizeBytes = null;
            backup.CompletedAtUtc = null;
            backup.RetentionDays = settings.RetentionDays;
            backup.ExpiresAtUtc = clock.GetUtcNow().AddDays(settings.RetentionDays);
            await database.SaveChangesAsync(cancellationToken);
            var version = await database.SyncHeads.Where(item => item.UserId == userId)
                .Select(item => (long?)item.Version).SingleOrDefaultAsync(cancellationToken) ?? 0;
            if (version < request.ExpectedSyncVersion)
                throw new BackupException(409, "backup_sync_incomplete", "The server has not received the required synchronized account version.");
            var payload = await BackupSnapshot.CaptureAsync(database, userId, backup.Id, clock.GetUtcNow(), version,
                settings.MaxPayloadBytes, cancellationToken);
            try
            {
                BackupDocument.Read(payload, userId, backup.Id, version);
                backup.Payload = protector.Protect(userId, backup.Id, backup.FormatVersion, payload);
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
            cancellationToken.ThrowIfCancellationRequested();
            backup.SizeBytes = backup.Payload.LongLength;
            backup.SyncVersion = version;
            backup.CompletedAtUtc = clock.GetUtcNow();
            backup.ExpiresAtUtc = backup.CompletedAtUtc.Value.AddDays(settings.RetentionDays);
            backup.Status = "Completed";
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Backup {BackupId} completed with format {FormatVersion}, {SizeBytes} stored bytes and expiry {ExpiresAtUtc}",
                backup.Id, backup.FormatVersion, backup.SizeBytes, backup.ExpiresAtUtc);
            return new BackupCreation(ToMetadata(backup), false);
        }
        catch (Exception exception)
        {
            var code = exception is BackupException known ? known.Code :
                exception is OperationCanceledException ? "backup_interrupted" : "backup_storage_failed";
            database.ChangeTracker.Clear();
            if (attempted is not null)
            {
                var committed = await RecordFailureAsync(attempted, code);
                if (committed is not null) return new BackupCreation(committed, true);
                logger.LogWarning("Backup {BackupId} failed with code {FailureCode} ({FailureType})", attempted.Id, code, exception.GetType().Name);
            }
            if (exception is OperationCanceledException) throw;
            if (exception is BackupException failure)
                throw new BackupException(failure.StatusCode, failure.Code, failure.Message, attempted?.Id);
            throw new BackupException(503, "backup_storage_failed", "Backup storage could not be confirmed. Retry using the same request ID.", attempted?.Id);
        }
    }

    public async Task<BackupMetadata?> GetAsync(Guid userId, Guid backupId, CancellationToken cancellationToken = default)
    {
        var row = await database.UserBackups.AsNoTracking().Where(item => item.UserId == userId && item.Id == backupId)
            .Select(MetadataProjection).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToMetadata(row);
    }

    public async Task<byte[]> DownloadAsync(Guid userId, Guid backupId, CancellationToken cancellationToken = default) =>
        (await ReadSnapshotAsync(userId, backupId, cancellationToken)).Payload;

    internal async Task<(byte[] Payload, BackupDocument Document)> ReadSnapshotAsync(Guid userId, Guid backupId,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        byte[]? payload = null;
        try
        {
            var row = await database.UserBackups.AsNoTracking().Where(item => item.UserId == userId && item.Id == backupId)
                .Select(item => new { item.Status, item.ExpiresAtUtc, item.FormatVersion, item.SyncVersion, item.SizeBytes,
                    PayloadLength = item.Payload == null ? 0 : item.Payload.Length }).SingleOrDefaultAsync(cancellationToken);
            if (row is null) throw new BackupException(404, "backup_not_found", "Backup not found.");
            if (row.ExpiresAtUtc <= clock.GetUtcNow()) throw new BackupException(410, "backup_expired", "This backup has expired.");
            if (row.Status != "Completed") throw new BackupException(409, "backup_not_completed", "This backup is not completed.");
            BackupSnapshot.ValidateVersion(row.FormatVersion);
            if (row.PayloadLength > settings.MaxPayloadBytes + 1024)
                throw new BackupException(413, "backup_too_large", "The backup exceeds the configured size limit.");
            if (row.PayloadLength == 0 || row.SizeBytes != row.PayloadLength || row.SyncVersion is null)
                throw new BackupException(400, "backup_invalid", "The backup storage metadata is inconsistent.");
            var encrypted = await database.UserBackups.AsNoTracking().Where(item => item.UserId == userId && item.Id == backupId)
                .Select(item => item.Payload!).SingleAsync(cancellationToken);
            payload = protector.Unprotect(userId, backupId, row.FormatVersion, encrypted);
            if (payload.Length > settings.MaxPayloadBytes)
                throw new BackupException(413, "backup_too_large", "The backup exceeds the configured size limit.");
            return (payload, BackupDocument.Read(payload, userId, backupId, row.SyncVersion.Value));
        }
        catch (Exception exception)
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            logger.LogWarning("Backup {BackupId} read failed ({FailureType})", backupId, exception.GetType().Name);
            if (exception is BackupException or OperationCanceledException) throw;
            if (exception is CryptographicException)
                throw new BackupException(422, "backup_unreadable", "The backup could not be authenticated or decrypted.", backupId);
            throw new BackupException(503, "backup_storage_failed", "Backup storage is unavailable. Retry later.", backupId);
        }
    }

    public async Task<BackupPage> ListAsync(Guid userId, int page, int limit, CancellationToken cancellationToken = default)
    {
        if (page is < 1 or > 10000 || limit is < 1 or > 100)
            throw new BackupException(400, "invalid_backup_page", "Page must be 1–10000 and limit must be 1–100.");
        var rows = await database.UserBackups.AsNoTracking().Where(item => item.UserId == userId)
            .OrderByDescending(item => item.CreatedAtUtc).ThenBy(item => item.Id)
            .Skip((page - 1) * limit).Take(limit + 1).Select(MetadataProjection).ToListAsync(cancellationToken);
        return new BackupPage(rows.Take(limit).Select(ToMetadata).ToList(), rows.Count > limit ? page + 1 : null);
    }

    public async Task<BackupStatus> StatusAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var row = await database.UserBackups.AsNoTracking().Where(item => item.UserId == userId &&
                item.Status == "Completed" && item.ExpiresAtUtc > now && item.FormatVersion == BackupSnapshot.CurrentFormatVersion)
            .OrderByDescending(item => item.CompletedAtUtc).ThenBy(item => item.Id)
            .Select(MetadataProjection).FirstOrDefaultAsync(cancellationToken);
        return new BackupStatus(row is null ? null : ToMetadata(row), settings.RetentionDays, settings.MaxPayloadBytes, settings.IsConfigured);
    }

    private void EnsureConfigured()
    {
        if (!settings.IsConfigured)
        {
            logger.LogWarning("Backup operations unavailable: durable key-ring configuration or backup limits are invalid");
            throw new BackupException(503, "backups_unavailable", "Backup retention, size limits and a durable encryption key-ring path must be configured.");
        }
    }

    private async Task<BackupMetadata?> RecordFailureAsync(UserBackup attempted, string code)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var existing = await database.UserBackups.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == attempted.UserId &&
                item.RequestId == attempted.RequestId, timeout.Token);
            if (existing is not null && (existing.RequestedSyncVersion != attempted.RequestedSyncVersion || existing.FormatVersion != attempted.FormatVersion))
                return null;
            if (existing?.Status == "Completed") return ToMetadata(existing);
            if (existing is null)
            {
                attempted.Status = "Failed";
                attempted.Payload = null;
                attempted.SizeBytes = null;
                attempted.SyncVersion = null;
                attempted.CompletedAtUtc = null;
                attempted.FailureCode = code;
                attempted.User = null!;
                database.UserBackups.Add(attempted);
                await database.SaveChangesAsync(timeout.Token);
            }
            else
                await database.UserBackups.Where(item => item.Id == existing.Id && item.UserId == attempted.UserId && item.Status != "Completed")
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, "Failed").SetProperty(item => item.FailureCode, code)
                        .SetProperty(item => item.Payload, (byte[]?)null).SetProperty(item => item.SizeBytes, (long?)null)
                        .SetProperty(item => item.CompletedAtUtc, (DateTimeOffset?)null).SetProperty(item => item.SyncVersion, (long?)null), timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Backup {BackupId} failure metadata could not be stored ({FailureType}); completion is unconfirmed",
                attempted.Id, exception.GetType().Name);
        }
        finally { database.ChangeTracker.Clear(); }
        return null;
    }

    private BackupMetadata ToMetadata(UserBackup row)
    {
        var expired = row.ExpiresAtUtc <= clock.GetUtcNow();
        return new BackupMetadata(row.Id, expired ? "Expired" : row.Status, row.FormatVersion, row.CreatedAtUtc,
            row.CompletedAtUtc, row.ExpiresAtUtc, row.RetentionDays, row.SyncVersion, row.SizeBytes, row.FailureCode, expired,
            !expired && row.Status == "Completed" && row.FormatVersion == BackupSnapshot.CurrentFormatVersion && settings.IsConfigured);
    }

    private static readonly Expression<Func<UserBackup, UserBackup>> MetadataProjection = row => new UserBackup
    {
        Id = row.Id, UserId = row.UserId, Status = row.Status, FormatVersion = row.FormatVersion,
        CreatedAtUtc = row.CreatedAtUtc, CompletedAtUtc = row.CompletedAtUtc, ExpiresAtUtc = row.ExpiresAtUtc,
        RetentionDays = row.RetentionDays, SyncVersion = row.SyncVersion, SizeBytes = row.SizeBytes, FailureCode = row.FailureCode
    };
}
