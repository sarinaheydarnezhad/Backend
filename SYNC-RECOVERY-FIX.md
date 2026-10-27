# Backup, sync and guest-copy recovery

## Scope and confirmed causes

The working tree was clean before this task. Only this backend repository is changed. No production endpoint, database, migration, configuration or key ring was accessed. No mobile repository was accessed. No device screenshots or production traces were available, so this is not a claim that a particular production incident has been reproduced.

- Separate 50-change pushes could leave a guest transfer partially committed. A bounded atomic guest-copy endpoint reuses the existing sync implementation, transactions, owner lock and receipts; no staging system or new database table is introduced.
- The initial sync migration left legacy data at version zero without feed entries. Safe zero-head bootstrap now publishes authoritative owned records transactionally and sets their versions without changing contents/timestamps. Inconsistent history is refused instead of guessed. Missing versions in an advanced feed now return an explicit conflict instead of endless non-progressing pulls.
- Refresh consumed the old token before persisting its replacement without a shared transaction. Both steps now commit or roll back together. Deliberate consumed-token replay protection is unchanged.
- Sync database failures and backup metadata-read failures previously reached generic HTTP 500 responses. They now return safe retryable 503 responses. Application diagnostic logs include exception type/correlation ID, not exception messages, credentials, tokens or learning payloads.
- The guest-copy regression's HTTP 500 was caused by SQLite ordering `DateTimeOffset`, not failed guest writes. UTC-tick conversions for the relevant ordered properties are confined to the sync test model; production SQL Server mappings are unchanged.
- Tracked published assemblies predated the backup fix. `deploy/Publish-Backend.ps1` builds a fresh staged publish, then refreshes assemblies/dependencies while preserving existing deployment JSON/web configuration. Build failure does not copy staged files. This is a local packaging tool, not an atomic live-deployment mechanism.

## Additive mobile API integration

`POST /api/v1/sync/guest-migration` requires the same current JWT/session as ordinary sync. Existing push/pull, backup schemas, default capability flags and quota responses remain supported. The new endpoint is necessary because multiple already-committed HTTP requests cannot be made atomic retroactively.

Request and HTTP 200 response schemas are the existing `SyncPushRequest` and `SyncPushResponse`. Send `{ "changes": [...] }`, using the same typed change objects as `/sync/push`; receive `{ "cursor": ..., "changes": [...] }` with per-change acknowledgements.

Limits are 1–10000 changes, 10485760 request bytes, and existing per-change payload limits. Unknown-length bodies are bounded while streaming. Payloads must contain only:

1. New decks with expected version zero, followed by their new cards.
2. For reviewed cards: unique immutable events in historical order, with a compatible complete box progression starting at box 1. Do not include a mutable review-state seed for that card.
3. For unreviewed cards: an optional box-1 state with zero counters and no last-review timestamp.

Every card's deck and every event/state's card must be present earlier in the same request. Settings, archival, repeated entity identities and existing-entity updates are rejected. Free/Plus limits and authenticated ownership remain authoritative; same-name decks and identical-text cards are not merged.

The mobile integration must persist the complete import plan before POST, including per-account mapped entity UUIDs and original device/change UUIDs, payloads, timestamps and expected versions. After a timeout or lost response, replay exactly that complete plan. Each change is durably acknowledged as `applied` or `duplicate`; duplicate replay does not increment the cursor or apply events again. A committed replay may return a newer account cursor if other edits have occurred; it does not overwrite those edits. Successful HTTP acknowledgement means the transaction committed, not that a device applied the resulting pull.

Retain guest originals until all acknowledgements, owned inventory, review state and pulls through the returned cursor have been verified. On 401 authenticate again; serialize refresh attempts and persist replacement credentials securely. On 403 stop and resolve entitlement limits; on 409 retain data and resolve the conflict. On 413 retain the plan; do not silently split it into a supposedly atomic migration. On 429 respect `Retry-After`; on 503 or uncertain transport failure retain identities and retry safely. Legacy multi-push clients remain per-batch atomic only.

New explicit error: 409 `{ error, code: "sync_history_incomplete" }` requires an audited authoritative repair for inconsistent/advanced history. Existing 400 `{ error }`, 409 `{ error, conflict }` and 403 entitlement bodies remain unchanged. Guest-plan validation can return 400 `{ error, code: "invalid_guest_migration" }`. Retryable account-storage failures return 503 `{ error, code: "storage_unavailable", traceId }` with `Retry-After: 5`; backup metadata failures retain `{ error, code: "backup_storage_failed", backupId }`.

Zero-head bootstrap is a write performed during the first successful pull/push, under a transaction and the owner lock. History replay must match saved counters, box, due date and last review before a legacy reviewed card is exposed. No immutable event is updated/deleted. Bootstrap is retried transactionally after storage failure. This does not repair arbitrary direct SQL edits, missing advanced history, progress resets with incompatible history, or unknown original arrival order. Such cases stop with 409 and preserve the originals.

## Database and deployment

No new EF migration or environment variable is required. Apply already-existing pending migrations through the controlled deployment process, including the sync, backup, subscription and authentication migrations. Do not run a migration against the design-time factory's default local database and assume production was updated.

`Backups__KeyRingPath` remains required and must reference an absolute, protected, writable durable mount. Configure the actual Liara mount through deployment administration; no guessed or ephemeral production path is supplied. Keep the existing application name `Lerona.Backups`, encryption keys and any relevant Windows service identity/DPAPI recovery material. All replicas must be able to use the same protected key ring. Existing SQL/JWT secrets remain deployment-managed. A missing path intentionally still fails closed with 503 `backups_unavailable`; regenerating keys cannot decrypt old artifacts.

Restore packages before running `powershell -File deploy/Publish-Backend.ps1`. The script uses `dotnet publish --no-restore`, stages under ignored `.dotnet/`, and preserves existing `appsettings*.json`, `web.config` and `liara.json`. It does not deploy or apply migrations. Deploy the complete refreshed directory through the normal release mechanism; do not copy assemblies over a running instance one by one. Persistent configuration and key-ring mounts must survive release replacement.

Backup capacity/cleanup policy is intentionally unchanged: failed and expired rows count toward the configured stored-artifact limit. Schedule the documented bounded expired-artifact cleanup operationally; this patch does not silently delete retained backups or remove idempotency receipts. `/health` remains liveness-only, not proof of SQL/key persistence readiness.

## Changed source files

| Files | Purpose |
| --- | --- |
| `backend/Flashcards.Api/Endpoints/V1Endpoints.cs` | Atomic guest route; shared bounded parsing; compatible sync errors; safe storage failures and completion diagnostics. |
| `backend/Flashcards.Api/Endpoints/BackupEndpoints.cs` | Safe retryable backup metadata failures. |
| `backend/Flashcards.Infrastructure/Sync/GuestMigrationValidator.cs` | Validate the entire create-only guest graph/history before writes. |
| `backend/Flashcards.Infrastructure/Sync/SyncBootstrap.cs` | Shared owner-head locking and guarded transactional legacy bootstrap. |
| `backend/Flashcards.Infrastructure/Sync/SyncService.cs` | Shared atomic batch execution, rollback tracking cleanup, bootstrap and feed-gap detection. |
| `backend/Flashcards.Infrastructure/Sync/SyncContracts.cs` | Optional typed error codes/status, preserving existing error bodies. |
| `backend/Flashcards.Infrastructure/Sync/SyncValidator.cs` | Separate guest batch bound; reject default last-review timestamps inconsistent with valid backups. |
| `backend/Flashcards.Infrastructure/Auth/TokenService.cs` | Atomic refresh consumption/replacement and rollback cleanup. |
| `backend/Flashcards.Infrastructure/Backups/BackupService.cs`, `BackupRestoreService.cs` | Reuse owner-head locking, including safe missing-head initialization. |
| `backend/Flashcards.Tests/SyncTests.cs`, `GuestMigrationTests.cs` | Fix SQLite ordering; guest success/replay/rollback/cancellation/authorization/quotas/concurrency/bootstrap/error regressions. |
| `backend/Flashcards.Tests/AuthTests.cs`, `AuthRecoveryTests.cs` | Refresh failure injection and recovery regression. |
| `backend/Flashcards.Tests/BackupTests.cs`, `BackupRecoveryTests.cs` | Backup metadata storage failure/recovery coverage. |
| `backend/Flashcards.Tests/DeckTests.cs`, `SubscriptionTests.cs` | Assert the documented retryable storage response and authoritative bootstrap feed counts; retain rollback, quota and one-query idle-pull regressions. |
| `deploy/Publish-Backend.ps1` | Reproducible staged packaging without replacing deployment settings. |
| `backend/publish/Flashcards.Api.dll`, `Flashcards.Api.exe`, `Flashcards.Api.pdb` | Refreshed API assembly, app host and symbols, including the guest and backup routes. |
| `backend/publish/Flashcards.Infrastructure.dll`, `Flashcards.Infrastructure.pdb` | Refreshed service implementations and matching symbols. |
| `backend/publish/Flashcards.Application.dll`, `Flashcards.Application.pdb`, `Flashcards.Domain.dll`, `Flashcards.Domain.pdb` | Matching rebuilt application/domain assemblies and symbols. |
| `GUEST-MIGRATION.md`, `BACKUPS.md`, `README.md`, this report | Current contract, deployment prerequisites and actual verification. |

## Verification

SDK 10.0.401 / runtime 10.0.12. Tests use synthetic isolated SQLite and ephemeral test encryption providers, not production SQL Server or production keys. Process-local CLI home and NuGet settings point at workspace caches.

Common command prefix:

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.nuget/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
```

Baseline:

```powershell
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~SyncTests|FullyQualifiedName~BackupTests|FullyQualifiedName~AuthTests' --nologo -v minimal
```

Executed before edits: **141 passed, 1 failed, 142 total**. The failure was the existing guest-copy foreign-card query HTTP 500 at `SyncTests.cs:363`. Log: `.dotnet/sync-recovery-baseline.log`.

The first post-change compilation exposed tuple-element naming errors in the new validator; these were corrected before tests could execute. The next focused run executed **158 tests, all passed**, before the final legacy-history consistency tests/guard were completed. Log: `.dotnet/sync-recovery-verified.log`.

The first complete regression run executed **265 tests: 259 passed, 6 failed** (`.dotnet/sync-recovery-final.log`). These failures were investigated as changes introduced by this repair: two old HTTP-500 assertions needed the explicitly documented retryable 503 contract; three subscription cases assumed legacy rows never entered the feed or seeded contradictory versions; and the idle zero-head pull performed unnecessary bootstrap reads. Tests now count actual bootstrap records and seed coherent zero-version legacy data, while production pull avoids bootstrap for empty caught-up accounts and retains a one-query fast path. The complete suite is rerun after these corrections; this failed run is not represented as passing.

Final complete regression command:

```powershell
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --nologo -v minimal
```

**Executed successfully: 265 passed, 0 failed, 0 skipped**, duration 4m15s. Log: `.dotnet/sync-recovery-final-verified.log`. This includes the guest rollback/cancellation/replay tests, separate-connection concurrent guest requests, owner isolation, legacy bootstrap/history safeguards, backup creation/restore/metadata failures, authentication and existing regression suites. It is SQLite integration coverage, not live SQL Server concurrency verification.

Final Release packaging command:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\Publish-Backend.ps1
```

**Exit 0**. Log: `.dotnet/sync-recovery-publish-final.log`. The process-only execution-policy override was approved after Windows blocked ordinary script execution; no persistent machine policy was changed. SHA-256 checks confirmed every pre-existing published appsettings/Liara/web configuration file was unchanged. All four published application DLLs exactly match the final Release build.

The final packaged API was started locally with synthetic process-only authentication/SQL settings using `dotnet Flashcards.Api.dll --urls http://127.0.0.1:51761`. HTTP checks against that local process returned:

- `GET /api/v1/status`: **200**.
- `POST /api/v1/sync/guest-migration`: **401** without credentials.
- `POST /api/v1/backups/{id}/restore`: **401** without credentials.
- `GET /api/v1/backups/{id}/download`: **401** without credentials.

These checks confirm the final packaged routes exist and are protected, not that database-backed operations work on Liara. They used `Invoke-WebRequest` against localhost, no real credentials and no real SQL connection. The temporary local server was stopped afterward. Earlier helper attempts encountered host PowerShell API/environment limitations; the final direct-process checks above actually executed successfully.

`git diff --check` passes. Build warnings remain for transitive `System.Security.Cryptography.Pkcs` 9.0.11 advisories, redundant references and EF reference resolution between the existing API Design 10.0.13 package and EF packages 10.0.11; project/package references were not changed by this task.

Remaining verification: SQL Server deadlock/locking behavior across replicas, real persistent encryption-key recovery, actual Liara release/configuration and physical-device integration. A completed refresh whose response is lost still requires reauthentication rather than weakening consumed-token replay protection. API callers must not run competing refreshes. Oversized guest plans, unsupported scheduler/session data and inconsistent advanced history require a separately designed or audited recovery flow; they are not silently truncated, split or reset.
