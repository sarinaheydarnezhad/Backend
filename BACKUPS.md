# Server-side learning backups

## Architecture and deployment

This is the single retained-backup implementation. Existing sync remains the only transport for offline account edits; backup creation reads the server's current learning data and does not import a mobile database. Guest data is never included. The target remains .NET 10 / EF Core 10.

Apply `backend/Flashcards.Infrastructure/Persistence/Migrations/20261015091415_AddUserBackups.cs` before enabling clients. It adds only `UserBackups`, its owner foreign key, constraints and indexes. Existing user/deck/card/review/auth tables are not altered. The pre-existing local `launchSettings.json` changes were not modified by this task.

Configuration uses normal ASP.NET Core configuration providers:

| Property / environment variable                                        | Shipped policy / requirement                                                                                 |
| ---------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| `Backups:RetentionDays` / `Backups__RetentionDays`                     | 30 days; configurable from 1 to 3650                                                                         |
| `Backups:MaxPayloadBytes` / `Backups__MaxPayloadBytes`                 | 10485760 bytes of UTF-8 JSON; configurable from 1024 to 104857600                                            |
| `Backups:MaxStoredBackupsPerUser` / `Backups__MaxStoredBackupsPerUser` | 20 records, including failed and expired artifacts until operational cleanup; configurable from 1 to 1000    |
| `Backups:KeyRingPath` / `Backups__KeyRingPath`                         | **Required**, absolute durable directory outside the source tree and web root; not configured by this change |

Incomplete/invalid backup configuration fails creation closed with 503 `backups_unavailable`, without changing authentication or learning data. Supply the existing JWT signing key and SQL connection configuration as usual. This change does not configure an API URL, provision SQL Server, apply migrations to a live database, or deploy a service.

Payloads are encrypted and integrity-protected using ASP.NET Core Data Protection, purpose-bound to format version, owner UUID and backup UUID. The SQL `varbinary(max)` contains ciphertext; byte size is the actual ciphertext size, not the plaintext limit. The service clears its temporary serialized byte array after encryption. Metadata is separate and never includes passwords, tokens or learning contents.

The durable Data Protection key ring is essential: losing it can make retained artifacts unreadable. On Windows the configured key files are protected with current-user DPAPI: preserve the Windows service identity/profile and key ring together. On non-Windows hosts, this implementation does not add a certificate/KMS key-file encryptor; restrict directory permissions, use encrypted storage and include protected keys in the operator's disaster-recovery process. Do not share Windows DPAPI key files across unrelated machine/service identities. Multi-host key distribution/protection must be designed and verified before scaling across hosts; SQL rows alone are not a complete disaster-recovery backup. No paid storage service is introduced. Framework Data Protection is referenced through `Microsoft.AspNetCore.App`, not a new authentication mechanism or independent cryptography implementation.

## Authenticated API

All routes are under `/api/v1/backups`, require an active existing JWT session, and return `Cache-Control: no-store`. Every read and write derives the owner from validated claims. There is no client-authoritative `userId`. Download and explicit non-destructive restore are now supported; see `BACKUP-RESTORE-FIX.md` for the investigation and release requirements.

- `POST /api/v1/backups/`: JSON `{ "requestId": "<UUID>", "formatVersion": 1, "expectedSyncVersion": 0 }`. Returns 201 for a committed snapshot, or 200 for the same completed request. The expected version is a minimum server sync cursor, not an instruction to rewind data. Creation is rate limited to five requests/minute per authenticated owner.
- `GET /api/v1/backups/{backupId}`: owned metadata; foreign and unknown IDs both return 404.
- `GET /api/v1/backups/status`: `{ latestCompleted, retentionDays, maxPayloadBytes, restoreSupported: false, automaticCleanup: false }`. Latest completed excludes expired and unsupported-format snapshots.
- `GET /api/v1/backups/?page=1&limit=25`: `{ items, nextPage }`; page 1–10000, limit 1–100, newest creation first, UUID tie-breaker. Pages can shift while new snapshots are created.
- `GET /api/v1/backups/{backupId}/download`: validates and decrypts the owned, completed, unexpired format-1 artifact and returns a JSON attachment. The exported file contains private learning data in plaintext: store it securely. This is an export, not an upload/import contract.
- `POST /api/v1/backups/{backupId}/restore`: `{ "mode": "mergeMissing", "expectedSyncVersion": 42 }`. Returns 200 with `{ backupId, mode, cursor, decks, cards, reviewStates, reviewEvents, settings, skippedDecks, skippedCards, skippedSettings }`. The version must equal the current server cursor. Counts describe inserted or preserved records, not a promise to overwrite newer data. Only retained server artifacts are accepted; arbitrary uploaded JSON is not accepted.

Existing create, metadata, list and status responses preserve their legacy `canRestore: false` / `restoreSupported: false` values by default because existing mobile validators require literal false. Updated clients opt into accurate backend capability flags with `?includeRestoreCapability=true` on these routes (including POST creation). Eligible completed format-1 metadata then advertises `canRestore: true`; failed, expired, unsupported and unconfigured artifacts do not. This query flag affects only capability reporting, never authorization. New restore clients must understand the merge strategy and authoritative restored review-state sync checkpoints described below.

Create and restore JSON request bodies are capped at 4096 bytes, including bodies without Content-Length. Required constructor fields must be present. Creation, download and restoration share the existing five-per-minute, per-owner expensive-operation limit. Snapshot content remains bounded by `Backups:MaxPayloadBytes`; encrypted payload size is checked before loading/decrypting it. Known restore/download errors retain `{ error, code, backupId }`: 400 for invalid documents/unsupported versions/requests, 404 for unknown or foreign artifacts, 409 for incomplete artifacts, stale account versions or identity conflicts, 410 for expiry, 413 for size limits, 415 for non-JSON requests, 422 for failed ciphertext authentication/decryption, and 503 for storage or unconfirmed restore failures. Existing entitlement errors remain 403 `{ error, code, limits, usage }`.

Metadata fields: `id`, `status`, `formatVersion`, `createdAtUtc`, nullable `completedAtUtc`, `expiresAtUtc`, `retentionDays`, nullable `syncVersion`, nullable `sizeBytes`, nullable safe `failureCode`, `isExpired`, and `canRestore: false`. Expiration is calculated from successful completion in UTC; retries do not renew an existing artifact's deadline. `createdAtUtc` remains the original attempt's time. Failure artifacts expire from their attempt time under the same configured policy.

Known errors use `{ error, code, backupId }`: 400 for malformed requests/unsupported formats/pagination, 409 for request-parameter conflicts, insufficient sync version or stored-artifact capacity, 410 for reuse of an expired request, 413 for oversized snapshots, and 503 for unavailable configuration or unconfirmed storage. Framework JSON binding/validation can return ProblemDetails. Rate-limit rejection is 429. Failures expose stable codes, not database messages or connection strings. Logs identify the backup, failure code and exception type without full exception messages, payloads or credentials.

## Format version 1 and consistency

The UTF-8 JSON envelope contains `formatVersion`, `backupId`, `userId`, `capturedAtUtc`, `syncVersion`, and these explicitly projected collections:

- `decks`: IDs, descriptions, language/typography, creation/update/archive timestamps and sync versions, including archived decks.
- `cards`: IDs and parent deck IDs, front/meaning/phonetic/category/example JSON, timestamps, archive state and sync versions, including archived cards.
- `reviewStates`: card ID, Leitner box/due date/last review, counters, creation/update timestamps and sync version.
- `reviewEvents`: immutable event ID, deck/card IDs, result/boxes, review/creation timestamps, optional study-session reference and optional `serverVersion` recording the original sync order. Older format-1 artifacts without this field remain accepted.
- `settings`: existing user-owned learning/UI/speech/reminder settings, timestamps and sync version.

No `User`, session, OTP or refresh-token entity is serialized. Device-local study-session records/queues and non-Leitner scheduler metadata do not exist in the server model and are **not** backed up; a review event's `studySessionId` is only a reference, not a saved session. Sync receipts/device IDs are not a restore/replay plan and are intentionally excluded. A future format change must explicitly support its version rather than treating arbitrary JSON as valid.

Creation is synchronous and uses a serializable transaction. It takes the same per-user sync-head write lock used by sync push through a no-op version update, then reads owner-filtered rows, checks the minimum version and stored-artifact capacity, serializes through a size-limited stream, validates the complete snapshot, encrypts, and commits payload plus Completed metadata atomically. Inconsistent source data is rejected rather than stored as an unusable Completed artifact. No deck, card, progress, event, setting or feed entry is modified, and the head version does not advance. Locks may block concurrent writes while the snapshot is captured. SQL Server concurrency/deadlock behavior still needs live-provider verification; deadlock/storage uncertainty is not treated as success.

Pending exists inside this transaction, not as a durable background-job promise. Failure rolls the transaction back and attempts to record Failed metadata with a short independent cancellation budget and no payload. Cancellation never commits a partial Completed artifact. A process crash before commit can leave no metadata at all; a storage outage can also prevent failure metadata from being recorded. Logs and a retry are then required. A completed artifact is never downgraded by failure reporting.

The unique `(UserId, RequestId)` index plus the transaction makes retries owner-scoped. Use the same UUID and parameters after a timeout/lost response; an already committed artifact is returned unchanged. Failed attempts can retry the same ID. Changed parameters conflict; expired IDs return 410. Once an operator deletes expired metadata, that old key no longer provides deduplication, so the idempotency window is bounded by stored metadata.

## Retention and cleanup

At `ExpiresAtUtc <= now`, metadata reports `Expired`, `isExpired: true`, and never advertises restore. Expired artifacts are excluded from latest-completed status. Expiration is implemented/tested; **physical deletion is not scheduled**. No SQL Agent job, hosted cleanup worker or external scheduler is installed.

Operators must schedule and verify cleanup with their existing SQL Server operational tooling. A narrowly scoped example, not executed by the app or this implementation:

```sql
DECLARE @now datetimeoffset(7) = SYSUTCDATETIME();
DELETE TOP (500) FROM dbo.UserBackups
WHERE ExpiresAtUtc <= @now AND Status IN ('Completed', 'Failed');
```

Run bounded batches with appropriate monitoring; it targets only expired backup artifacts/metadata, never live learning tables or encryption keys. Until cleanup is configured, expired ciphertext continues occupying storage and counts toward the per-user capacity. This is not a promise of automatic expiry deletion or guaranteed recovery from database/key-ring loss.

## Mobile integration and restore safety

The existing Back Up and Log Out choice drains/verifies the existing sync queue, submits a server backup, fetches its owned metadata again, rechecks synchronized local data, and only then logs out. A durable, account-scoped SecureStore reference contains only request UUID and sync cursor, permitting retry after a lost response or restart without duplicating artifacts. Account changes cannot reuse another owner's reference. Guest data is not uploaded. No-backup logout never calls this API.

The signed-out notice shows the confirmed artifact ID, configured retention and actual UTC expiration, including an expired notice after restart. Old sync-only logout receipts are not retroactively described as retained backups. Failure leaves the user signed in unless the existing authentication infrastructure determines that the session itself expired; explicit logout without backup remains available.

There was no previous restore strategy: restoration was deliberately unimplemented, not replace or merge. The new endpoint requires the explicit `mergeMissing` strategy. It never deletes, updates, unarchives, or renames existing decks/cards/settings. Existing progress/history are preserved together if either a state or any event already exists for that card; older backup history is never partially merged into newer progress. Backed-up review states and immutable events are restored for missing cards and for existing cards whose entire progress/history subtree is absent. `skippedCards` counts preserved card rows, even when an entirely missing progress subtree is recovered. Settings are inserted only when absent. This is missing-record recovery, not point-in-time replacement or undoing archival.

Before any restore inserts, the server authenticates/decrypts and validates the entire document: required collections/fields, duplicate JSON keys/identifiers, versions, owner/artifact binding, lengths/enums, JSON examples, counters/dates and deck/card/event relationships. Foreign-ID and reparenting conflicts abort the whole operation without identifying another owner. Current subscription limits apply to the final active dataset before inserts; an oversized backup is rejected atomically, not truncated. A serializable transaction takes the existing per-user sync-head write lock and commits all inserts, fresh monotonically increasing entity/feed versions and the head together. Backup timestamps are preserved through an insert-only save path, without changing normal write stamping or immutable-history guards. Failure/cancellation rolls back the transaction; retry after synchronizing is safe because already-present records are skipped.

Sync emits deck/card snapshots first, ordered review events next, authoritative review states after events, and missing settings last. New artifacts record original event versions. For old artifacts the existing owned feed supplies ordering when available; if neither source has versions, deterministic review-time/UUID order is used. Format 1 did not record enough information to reconstruct arbitrary original offline arrival order if the feed is also lost. The saved review-state checkpoint remains authoritative. Restore-capable clients must apply this later checkpoint even when events already exist and use it as the baseline for subsequent events rather than replaying pre-checkpoint history on top. Existing mobile code ignores states when any events exist; its restore support needs this targeted sync integration. No mobile files are changed here, and legacy backup/logout responses are preserved.

## Restore fix verification

See [BACKUP-RESTORE-FIX.md](BACKUP-RESTORE-FIX.md) for the current investigation, exact changed-file manifest, executed recovery/regression tests, deployment prerequisites and frontend compatibility requirements.

## Historical creation-only implementation

Backend additions: `backend/Flashcards.Domain/UserBackup.cs`; `backend/Flashcards.Infrastructure/Backups/BackupSettings.cs`, `BackupContracts.cs`, `BackupPayloadProtector.cs`, `BackupSnapshot.cs`, `BackupService.cs`; `backend/Flashcards.Api/Endpoints/BackupEndpoints.cs`; migration `20261015091415_AddUserBackups.cs` and its designer; `backend/Flashcards.Tests/BackupTests.cs`; this document and `BACKUP-VERIFICATION.md`.

Backend updates: `backend/Flashcards.Infrastructure/Persistence/FlashcardsDbContext.cs` and model snapshot; `backend/Flashcards.Infrastructure/DependencyInjection.cs` and `.csproj`; `backend/Flashcards.Api/Endpoints/V1Endpoints.cs`, `Program.cs`, `appsettings.json`; `backend/Flashcards.Tests/PersistenceTests.cs`; `README.md`.

Mobile additions: `src/core/sync/server-backup.ts` and `.test.ts`; `docs/server-backups.md`. Updates: cloud client, sync engine, logout journal, secure-logout tests, logout notice/screen tests, English/Persian messages, secure-logout docs and README. See the mobile document for exact paths. Actual commands, counts and remaining limitations are in [BACKUP-VERIFICATION.md](BACKUP-VERIFICATION.md).
