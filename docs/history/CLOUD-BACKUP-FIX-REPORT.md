Cloud backup, logout and account connection repair — 2026-10-27

The source fixes are complete. Production release **v38** is READY with the existing 1 GB durable `backup-keys` disk mounted at `/data/backup-keys`. The disk was initially attached successfully in v36; a later v37 deployment omitted its binding and detached it. v38 restores the same disk and keys, and deployment-root configuration plus a deployment wrapper now explicitly retain the binding. Separate production-side encryption/decryption processes passed with existing keys present, and `/health` returns 200. No database reset or migration was performed. Actual user backup completion must still be retried from an authenticated app session; verification did not create a user backup record.

**Causes and evidence**

| Reported failure | Verified cause | Status |
| --- | --- | --- |
| Backup Now does not work | The backend originally rejected encrypted snapshot creation because durable backup key-ring configuration was absent. Configuring a path without a disk then failed during key creation. | Confirmed from hosting configuration and live logs. The durable disk is now provisioned/mounted and production-side key encryption/decryption succeeds. Retry the user action to confirm a stored account backup. |
| Back Up and Log Out returns 503 | The same retained-snapshot storage dependency failed for both buttons. It was not a frontend-generated 503. | The mounted key-ring repair is live. Failure remains recoverable and does not log the user out. An authenticated app retry is still needed. |
| Existing account cannot connect after clearing local data | A fresh account database persisted automatic light-theme defaults as an ordinary settings edit. The sync insert trigger only excluded the old system-theme defaults. Initial account entry pushed this version-zero settings edit before downloading the account's already versioned settings, producing HTTP 409. | Reproduced against the real isolated .NET API using phone/password login and the account-entry preview. Corrected initialization downloads saved settings without an unsolicited push. |

Read-only Liara inspection identified `lerona-api` and found no `Backups__KeyRingPath` environment variable. Its mounted filesystems had no application backup-data disk. Live release v30 logs identify `BackupService` warnings on `/api/v1/backups/` at `2026-10-27T14:07:34`, `14:23:39` and `14:25:34`: “Backup operations unavailable: durable key-ring configuration or backup limits are invalid.” Times are recorded as displayed by Liara. The checked-in deployment settings also lacked a key-ring path. [Sanitized request diagnostics](C:/a/.tools/cloud-sync-live-diagnostics.json) retain this evidence without credentials or learning content.

The account-connection defect is connected to the reported reproduction through an actual HTTP regression: initializing an installation with the original ordinary defaults write returns a settings conflict from `/sync/push`; an explicit “keep server” choice resolves that already queued conflict and permits account entry. Initializing another fresh installation with the fix succeeds against the same account, retrieves its saved settings and learning data, and makes no initial push. The particular production connection-error response was not supplied, so this does not identify every possible error that may have appeared on that device.

An additional reproduced initial-download failure was fixed in the affected path: backend bootstrap/restore feeds can begin with an archived deck/card snapshot. The mobile client previously updated a nonexistent row, then failed a dependent foreign-key insert and rolled the cursor back. It now inserts a missing archived snapshot before its cards/review history. This was verified against real migrated SQLite; it is not claimed to be the proven cause of the particular production connection error.

**Flow distinctions preserved**

| Action | Requests and persistence | Completion behavior |
| --- | --- | --- |
| Sync Now | `POST /api/v1/sync/push`, `GET /api/v1/sync/pull`; durable operation IDs, per-entity versions, transactional local page application and cursor advancement | Synchronizes account rows. Does not create a retained backup or update the backup timestamp. |
| Backup Now | Synchronize all pending batches and pull pages; read back owned inventory and review state through `/me`; `POST /api/v1/backups/` with `{requestId, formatVersion: 1, expectedSyncVersion}`; verify `/backups/{id}` and synchronization again | Success requires a completed, persisted, compatible snapshot and matching receipt. The successful server timestamp is shown. Authentication remains active. |
| Back Up and Log Out | The same verified backup pipeline, followed by the established credential-clearing, guest-scope selection and session-revocation flow | Logout begins only after the backup succeeds. A backup failure retains the authenticated account scope and its local records. Retry uses the existing idempotency reference where applicable. |
| Account connection after data clear | Phone/password authentication, verified `/me` identity, isolated account storage, default initialization, account-entry preview, then sync push/pull | Untouched local defaults are placeholders; they cannot overwrite cloud settings. An empty cloud account is valid. Explicit guest transfer remains a separate confirmation. |

The backup service captures synchronized server rows, validates the document, encrypts it with the account/backup-specific data-protection purpose, stores `UserBackups.Payload` and completion metadata in SQL, and commits before returning success. Local data collection happens through the durable sync outbox; the backup button does not upload an unverified raw copy of the device database. An encrypted-snapshot failure does not imply that earlier successful sync uploads were undone.

Both retained-backup creation and the standalone backup download/restore API require durable encryption keys. Normal initial account connection uses the sync feed and does not call backup download/restore; therefore missing encryption keys do not explain its settings conflict.

**Implemented changes**

Frontend application files, under `C:\a\lm`:

- [repositories.ts](C:/a/lm/src/core/composition/repositories.ts): initialize device defaults without adding a user operation, while retaining the settings cache initialization.
- [sqlite-settings-repository.ts](C:/a/lm/src/features/settings/data/sqlite-settings-repository.ts): transactional default-only initializer; existing preferences are untouched, and a failure rolls back both the insert and queue-suppression flag. Explicit preference edits still use the normal queued update path.
- [sync-store.ts](C:/a/lm/src/core/sync/sync-store.ts): allow untouched defaults to remain local when the cloud account has no settings; explicit settings operations still require their acknowledged versions. Materialize missing archived snapshots before dependent history. Cursor/transaction safeguards remain intact.
- [backup-failure.ts](C:/a/lm/src/features/settings/presentation/backup-failure.ts), [settings-backup-card.tsx](C:/a/lm/src/features/settings/presentation/settings-backup-card.tsx), [logout-screen.tsx](C:/a/lm/src/features/settings/presentation/logout-screen.tsx), and [messages.ts](C:/a/lm/src/shared/localization/messages.ts): distinguish `backups_unavailable` from generic network/storage failures, show an actionable English/Persian explanation, and keep retry available. Failed attempts do not show a new successful timestamp or success confirmation.
- [guest-data-screen.tsx](C:/a/lm/src/features/auth/presentation/guest-data-screen.tsx): when an older attempt has already left a settings conflict, expose the explicit server/local settings choices on the connection screen and retry account entry after resolution. No existing queued edit is silently discarded.

Backend application/configuration files, under `C:\a\LeronaBackend\Backend`:

- [BackupEndpoints.cs](C:/a/LeronaBackend/Backend/backend/Flashcards.Api/Endpoints/BackupEndpoints.cs): keep typed failure codes, add a correlation `traceId`, and supply `Retry-After` on service-unavailable backup failures. Failure remains non-successful.
- [appsettings.Production.json](C:/a/LeronaBackend/Backend/backend/Flashcards.Api/appsettings.Production.json): propose `/data/backup-keys` as the durable key-ring path. This requires the matching reviewed mount; no ephemeral fallback is introduced.
- [liara.backups.json](C:/a/LeronaBackend/Backend/deploy/liara.backups.json): proposed `backup-keys` disk binding to `/data/backup-keys`.
- [Stage-CloudBackupRelease.ps1](C:/a/LeronaBackend/Backend/deploy/Stage-CloudBackupRelease.ps1): build an isolated local release, retain existing published settings, merge the missing key-ring setting, stage the matching disk descriptor, and refuse to replace a different retained key-ring path. This script performs no remote operation.
- [CLOUD-BACKUP-RECOVERY.md](C:/a/LeronaBackend/Backend/deploy/CLOUD-BACKUP-RECOVERY.md) and [BACKUPS.md](C:/a/LeronaBackend/Backend/BACKUPS.md): review/application instructions and current deployment requirements.

No database migration, data cleanup, authentication redesign, automatic sync scheduling change, or unrelated refactoring was introduced. In particular, old ambiguous queued edits are preserved rather than silently classified as defaults and deleted.

**Regression coverage and results**

New or extended tests:

- [account-initialization.test.ts](C:/a/lm/src/core/sync/account-initialization.test.ts): fresh defaults, preservation of explicit preference edits, failed initialization rollback, and archived deck/card downloads with preserved review history.
- [cloud-sync-backup.test.tsx](C:/a/lm/src/features/settings/presentation/cloud-sync-backup.test.tsx): configuration failures in English/Persian, no false success, and preservation of the last successful timestamp.
- [logout-screen.test.tsx](C:/a/lm/src/features/settings/presentation/logout-screen.test.tsx): actionable configuration failure and an available backup/logout retry.
- [guest-data-screen.test.tsx](C:/a/lm/src/features/auth/presentation/guest-data-screen.test.tsx): English/Persian recovery of an already queued settings conflict, without implicit selection, guest-data discard, or logout.
- [BackupFlowTests.cs](C:/a/LeronaBackend/Backend/backend/Flashcards.Tests/BackupFlowTests.cs): the live missing-configuration condition returns 503 with its code/correlation/retry header, writes no artifact, retains authentication, and can subsequently persist a backup when configured; a separate test reads a stored snapshot with a new provider opened against the persisted key files.
- [MobileCloudFlowTests.cs](C:/a/LeronaBackend/Backend/backend/Flashcards.Tests/MobileCloudFlowTests.cs) and [cloud-account-flows.e2e.ts](C:/a/lm/test/cloud-account-flows.e2e.ts): actual mobile sync/account code communicates with a localhost .NET/Kestrel API and isolated SQLite storage. They use the actual phone/password endpoints and verified account identity. Native secure storage and mobile SQLite adapters are replaced; API responses and encrypted backend snapshots are real. The test-only failure switch exists solely in the isolated test host.

| Required scenario | Evidence |
| --- | --- |
| Backup Now persists local changes | Read the retained snapshot back through the real download endpoint; assert saved decks, cards, settings, review events and progress. |
| Backup Now handles backend failure | Inject an actual backend protector/storage failure; assert HTTP 503, no new successful timestamp, retained local records/session, and successful retry. |
| Backup and logout succeeds | Verify the completed snapshot receipt before the logout request, then verify session revocation and credential clearing. |
| Failed backup/logout preserves local data | Assert unchanged account scope, valid credentials and existing cards after 503; retry succeeds. |
| Existing user can sign in after local clear | Clear local credentials/storage, use the actual phone login endpoint, and complete the account-entry preview. |
| Existing cloud data reaches a fresh install | Pull saved cards, settings and review progress into freshly migrated SQLite. |
| Empty cloud account initializes | Preview succeeds at cursor zero and an empty retained backup can be created/read. |
| Empty local data does not erase cloud data | Assert no unsolicited initial push and unchanged owned cloud inventory. |
| 503 cannot produce false success | Assert typed backend error, no completed artifact in the missing-config case, no success notice/timestamp, and no logout on failure. |

Frontend verification passed **335 tests across 22 suites**. The final complete backend suite passed **268 tests, zero failures and zero skipped**, including the durable-key and real HTTP integration tests. The separate real HTTP frontend suite passed **3 scenarios**; it is launched by that backend integration test, and its final rerun also verified recovery of a previously queued settings conflict. TypeScript checking, targeted ESLint checking and both repositories' whitespace checks passed. [Complete backend execution log](C:/a/.tools/cloud-sync-fix-backend-final.log). Backend tests use isolated SQLite, not production SQL Server.

Evidence: [frontend regression log](C:/a/.tools/cloud-sync-fix-frontend-final.log), [real HTTP frontend log](C:/a/lm/.tools/cloud-account-flows.http.log), [final HTTP recovery run](C:/a/.tools/cloud-sync-real-http-recovery-final.log), [focused backend/durable-key log](C:/a/.tools/cloud-sync-real-http-final.log), [typecheck log](C:/a/.tools/cloud-sync-fix-typecheck.log), [lint log](C:/a/.tools/cloud-sync-fix-lint.log).

Reproduce the backend suite, including the actual frontend/backend HTTP integration:

```powershell
# From C:\a\LeronaBackend\Backend
$env:LERONA_FRONTEND_DIR = 'C:\a\lm'
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --nologo -v minimal
```

Without `LERONA_FRONTEND_DIR`, that cross-project test is explicitly skipped; it is not silently treated as passing. The remaining backend tests run normally. Frontend test selection:

```powershell
# From C:\a\lm
node node_modules/jest/bin/jest.js --runInBand src/core/sync src/core/composition/sync-session.test.ts src/core/database/database.test.ts src/core/storage src/features/settings/presentation/cloud-sync-backup.test.tsx src/features/settings/presentation/logout-screen.test.tsx src/features/settings/data/sqlite-settings-repository.test.ts src/features/auth/presentation/guest-data-screen.test.tsx src/shared/localization/localization.test.ts
```

**Release preparation and remaining work**

The initial local Release build succeeded at [the published artifact directory](C:/a/LeronaBackend/Backend/.dotnet/cloud-backup-release-6679062bb5ab458f9c0a7aaaf8252b2f). [Stage verification](C:/a/.tools/cloud-sync-release-verification.json) confirms its four application DLLs matched their Release builds. That binaries-only directory is not a valid input to the native Liara .NET source builder: release v35 failed before activation with `Could not find a .csproj file`. Deployment was corrected to use all four source projects, and v36 is READY. The staging script now produces this required source layout. Existing tracked published binaries were not replaced.

The user approved disk provisioning and redeployment, and those actions are complete. The persistent disk is 1 GB. Existing SQL/JWT configuration and Warning-level logging were preserved; the container root remains read-only. The durable mount supplies writable key storage. No temporary-directory fallback or key deletion was used.

Backup and fresh-install recovery were verified end-to-end over local HTTP with stored snapshot readback. The backend's existing snapshot download/restore round-trip regressions also run in the complete suite. This is not verification on a physical phone or production SQL Server. The frontend changes require a new application build. The durable mount is now live and persisted-key encryption/decryption passed in separate production-side processes. Remaining user-session checks include authenticated backup/readback and physical-device TLS/networking and OS secure storage. No account-specific production data repair was performed.

Existing build warnings about package advisories and EF assembly-version resolution remain unchanged; they were not the reproduced causes and were not expanded into unrelated work. The earlier report's idle-sync scheduling observation was outside these three explicit flows and was not changed.

**Follow-up: read-only `/data` during encryption-key creation**

The later `IOException: Read-only file system: '/data'`, wrapped in a `CryptographicException` with `backup_storage_failed`, means the path is configured but the key files cannot be created. Read-only hosting inspection confirmed zero provisioned application disks and no `/data/backup-keys` filesystem mount. This is an infrastructure prerequisite, not another frontend upload error. [Current sanitized hosting state](C:/a/.tools/cloud-sync-current-hosting.json).

A packaging gap was corrected in the usual deployment directory: [backend/publish/liara.json](C:/a/LeronaBackend/Backend/backend/publish/liara.json) previously lacked the disk binding, while the alternate staging descriptor contained it. That file and [backend/publish/appsettings.Production.json](C:/a/LeronaBackend/Backend/backend/publish/appsettings.Production.json) now pair the same `backup-keys` disk mount with `/data/backup-keys`. JSON consistency checks and whitespace checks passed. No application code or binaries changed in this follow-up.

After approval, the live repair was completed: provisioned `backup-keys`, deployed source release v36 with `backup-keys:/data/backup-keys`, and confirmed `/dev/loop154 /data/backup-keys ext4 rw`. A synthetic encrypted probe was written and read by separate .NET processes using the persisted key ring; the probe and temporary helper were removed while preserving the key files. The public health and status routes return 200, anonymous sync still returns 401, and the checked new-release logs contained no read-only-filesystem or key-ring-read errors. No actual user-backup success is claimed until an authenticated retry.

Evidence: [deployment result](C:/a/.tools/backup-disk-deployment-result.json), [production key-ring verification](C:/a/.tools/backup-keyring-production-verification.json), [mount inspection](C:/a/.tools/backup-disk-active-mounts.log), and [source deployment log](C:/a/.tools/backup-disk-source-deployment.log). The failed v35 release never replaced the previous running application; v36 completed the repair.

**Follow-up: a later deployment detached the disk**

The error reported at 16:26–16:27 occurred after deployment v37, whose release metadata had `disks: []`. The disk resource still existed, but there was no container mount. This explains why redeploying the app/database did not resolve the filesystem exception. Release bindings do not automatically carry over when a new deployment omits them.

The same disk was reattached by v38; no replacement disk or key ring was created. The normal repository root [liara.json](C:/a/LeronaBackend/Backend/liara.json) and source root [backend/liara.json](C:/a/LeronaBackend/Backend/backend/liara.json) now declare the binding. [Deploy-Backend.ps1](C:/a/LeronaBackend/Backend/deploy/Deploy-Backend.ps1) stages the source release and supplies the disk flag explicitly. Use this command for future backend deployments:

```powershell
powershell -File C:\a\LeronaBackend\Backend\deploy\Deploy-Backend.ps1
```

The wrapper was executed successfully and v38 reached READY. The mount is `/dev/loop154 /data/backup-keys ext4 rw`; synthetic protection/unprotection succeeded in separate processes with `existingKeysPresent: true`. The probe/helper files were removed, and the retained key files were preserved. Health returned 200; the checked v38 logs contained no read-only-filesystem or key-ring-read errors. No user database records were used by the verification. [Before/after release bindings](C:/a/.tools/backup-mount-regression-result.json), [verified mount](C:/a/.tools/backup-mount-regression-verified-mounts.log), [deployment log](C:/a/.tools/backup-mount-regression-deploy.log), [production key verification](C:/a/.tools/backup-keyring-production-verification.json).
