# Backup and restore investigation — October 26–27, 2026

## Confirmed root causes and investigation boundaries

The existing implementation was a retained **creation-only** snapshot feature, not a working backup-and-restore workflow:

- `BackupEndpoints` mapped only create, status, list and owned metadata reads. There was no download or restore route. Both source and earlier `BACKUPS.md` explicitly disabled restoration; no replace/merge strategy existed to preserve.
- `IBackupPayloadProtector` exposed encryption only. No service decrypted, validated, imported or synchronized a saved snapshot back into learning tables. Completed metadata was therefore not evidence that recovery worked.
- Creation deliberately fails with 503 `backups_unavailable` without a fully qualified durable `Backups:KeyRingPath` and valid limits. No shipped appsettings file supplies that path. This prerequisite must be configured by deployment, not replaced by insecure ephemeral keys or a guessed local directory.
- The adjacent mobile checkout (`D:/app/lightman`) calls the existing create/read/status/list paths correctly, supplies all creation fields, and rejects any metadata whose `canRestore` is not literal false. Simply changing that flag to true would break successful backup/logout verification. Responses therefore remain legacy-compatible unless a new client explicitly opts into restore capability reporting.

The original 12 backup integration tests passed before editing. Configured server-side creation was not reproduced as broken. Available local logs are historical test artifacts, not deployed Lerona server logs; no relevant production backup exception trace, live configuration, deployed database or production key ring was supplied. Missing deployment configuration is a confirmed default behavior, **not a verified assertion about production environment variables**. A separate creation failure on a deployed device still requires its safe HTTP error code/trace ID and server-side diagnostics. No production database or real user data was read or modified.

## Workflow and data model

Creation uses the authenticated JWT subject and active session, a serializable SQL transaction and the existing sync-head write lock. It checks request UUID idempotency, minimum synchronized version, capacity and payload size; captures owner-filtered learning rows; validates the complete snapshot before declaring it recoverable; encrypts with the existing application/format/owner/artifact Data Protection purposes; then atomically stores ciphertext and completion metadata. Inconsistent source learning data produces a failed artifact, not an unusable Completed backup, without changing that source data. Failures roll back and attempt bounded failure-metadata recording. Metadata projections do not load payloads.

The snapshot includes archived and active decks/cards, deck appearance/language, card contents/examples, Leitner states/counters/dates, immutable reviews/study-session references and UI/study/speech/reminder settings. Deck ownership and review composite foreign keys constrain card/deck/user relationships. Accounts, passwords, tokens, OTPs, payment/subscription records and device-local queues are not recovery payloads. Server-side study-session entities do not exist; an event's session UUID is only a reference.

Existing metadata routes remain unchanged. Added routes:

- `GET /api/v1/backups/{backupId}/download` returns a validated owned JSON attachment over authenticated HTTPS, with no-store caching. Downloads contain plaintext private learning data and must be stored securely.
- `POST /api/v1/backups/{backupId}/restore` accepts `{ "mode": "mergeMissing", "expectedSyncVersion": <current cursor> }` and returns the new cursor plus inserted/skipped counts. It restores a retained server artifact, not arbitrary uploaded JSON.
- Existing create/read/list/status routes accept optional `includeRestoreCapability=true`. Only opted-in responses advertise actual eligible restore capability. Omitting the flag preserves the old literal-false mobile contract.

Creation and restore requests now require complete JSON fields and are capped at 4096 bytes even without Content-Length. Creation/download/restore share the existing owner-scoped five-per-minute limiter. Plaintext and encrypted storage lengths are bounded before large payload processing. Errors use existing backup error envelopes, with status/code details in `BACKUPS.md`; unexpected errors log only artifact IDs and exception types, never payloads, credentials or database exception messages.

## Restore safety and policy

There was no prior restore strategy. The explicit new policy is **merge missing**, not replace, title-based matching, undelete, or point-in-time rollback:

1. Decrypt/authenticate using the same purposes as original artifacts. Fully validate format, owner/artifact/version binding, required collections/fields, duplicate properties/IDs, field limits, enums, dates/counters, JSON examples and every relationship before restore inserts.
2. Take the existing per-user sync-head lock inside a serializable transaction. Reject a stale expected cursor, foreign identifier collision or card reparenting with 409 before inserting anything. Recheck final active usage against current subscription limits; never truncate a backup to satisfy quotas.
3. Insert only missing deck/card/settings rows, preserving original IDs, timestamps, archive state, content and relationships. Existing rows are neither overwritten nor deleted. Progress/history are restored together only when the entire subtree is absent; any existing state or event protects that card's whole progress/history subtree from partial historical merges.
4. Append fresh per-user sync changes/versions and update the head in the same transaction. Review events retain their original identities/content; they are never updated. A narrowly scoped insert-only context save preserves historical timestamps without changing normal timestamp stamping or weakening immutable-record guards.
5. Commit all inserts, feed entries and cursor atomically. Failure/cancellation rolls back all of them. Synchronize after an uncertain response and retry with the latest cursor: existing records are skipped, not duplicated.

Original format 1 remains supported, including legacy string-array examples and artifacts without the new optional review-event `serverVersion`. New snapshots preserve original review order using this field. Old snapshots can recover order from the owned sync feed; when both order sources are absent, deterministic review-time/UUID order is used. An authoritative review-state checkpoint follows restored events in the feed, preserving saved progress even when historical arrival order or pre-history cannot be reconstructed. Mutable progress counters are validated independently from immutable history: the existing sync contract permits progress resets while retaining older review events, so the backup must preserve both rather than reject or recompute the reset state.

## Frontend compatibility

No frontend files are changed. Existing backup creation, verification, status/list and logout calls continue receiving the old capability flags and need no request changes. A restore-capable client must:

- Opt into capability reporting, allow boolean capability flags, and call the new endpoint only after explicit merge-missing consent and synchronization.
- Treat returned inserted/skipped counts accurately; a successful merge can legitimately insert zero rows when current data already exists.
- Apply the authoritative later review-state checkpoint, even when immutable events exist, then use it as the baseline for later events. The currently inspected mobile sync store ignores states whenever events exist and replays all history from scratch. Enabling restore UI without this targeted client integration can show incorrect historical progress despite correct restored backend state.
- Pull restored changes through the returned cursor and handle 403/409/410/413/422/503 without deleting local data or treating failures as successful restoration.

## Files changed

- `backend/Flashcards.Api/Endpoints/BackupEndpoints.cs`: download/restore routes, bounded complete requests and backward-compatible capability opt-in.
- `backend/Flashcards.Infrastructure/Backups/BackupPayloadProtector.cs`: purpose-bound decryption.
- `backend/Flashcards.Infrastructure/Backups/BackupContracts.cs`: explicit restore request/result.
- `backend/Flashcards.Infrastructure/Backups/BackupService.cs`: bounded owned artifact loading, validation/download, capability calculation and safe diagnostics.
- `backend/Flashcards.Infrastructure/Backups/BackupSnapshot.cs`: optional original event version in snapshots.
- `backend/Flashcards.Infrastructure/Backups/BackupDocument.cs`: complete version-1 validation and typed snapshot records.
- `backend/Flashcards.Infrastructure/Backups/BackupRestoreService.cs`: preflight planning, non-destructive transactional restore and synchronization.
- `backend/Flashcards.Infrastructure/DependencyInjection.cs`: restore-service registration.
- `backend/Flashcards.Infrastructure/Persistence/FlashcardsDbContext.cs`: internal timestamp-preserving insert-only save path; no schema change.
- `backend/Flashcards.Infrastructure/Subscriptions/EntitlementService.cs`: final-dataset restore quota validation.
- `backend/Flashcards.Infrastructure/Sync/SyncService.cs`: share existing snapshot serializers internally, without changing ordinary push behavior.
- `backend/Flashcards.Tests/BackupTests.cs`, `backend/Flashcards.Tests/BackupRestoreTests.cs`: original regression coverage plus recovery/validation/ownership/rollback/compatibility integration tests.
- `README.md`, `BACKUPS.md`, this report: current API, behavior, operational and release documentation.

## Verification

Tests use isolated synthetic SQLite databases and an ephemeral test Data Protection provider, never production data. Runtime: .NET SDK 10.0.401 / runtime 10.0.12; EF Core 10.0.11.

- Pre-change focused baseline: **12 passed, 0 failed** (`.dotnet/backup-baseline.log`).
- First expanded integration run: **51 passed, 0 failed** (`.dotnet/backup-verified.log`), including full learning-data round trip, preservation of existing rows, malformed/tampered/duplicate/incompatible snapshots, foreign ownership/collisions, 600-card restoration, final quotas, stale cursors and injected post-save failure rollback.
- **Final patch verification: 68 passed, 0 failed, 0 skipped**, comprising all **59 backup tests** and 9 foundation tests, including reset progress alongside retained immutable history (`.dotnet/backup-restore-compatibility-final.log`, `.dotnet/TestResults/backup-restore-compatibility-final.trx`). Command: `dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~BackupTests|FullyQualifiedName~FoundationTests' --nologo -v minimal --logger 'trx;LogFileName=backup-restore-compatibility-final.trx' --results-directory .dotnet/TestResults`.
- Focused integration run before the final reset-progress compatibility case: **67 passed, 0 failed, 0 skipped**, including all 58 then-existing backup tests and 9 foundation tests (`.dotnet/backup-restore-focused-final.log`).
- Latest complete regression run, before the final reset-progress compatibility adjustment: **244 passed, 1 failed, 0 skipped** out of 245 (`.dotnet/backup-restore-release.log`, `.dotnet/TestResults/backup-restore-release.trx`). Command: `dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --nologo -v minimal --logger 'trx;LogFileName=backup-restore-release.trx' --results-directory .dotnet/TestResults`. The sole failure was `SyncTests.Guest_copy_retries_preserve_distinct_cards_and_historical_progress_with_owner_only_reads`, returning HTTP 500 at `SyncTests.cs:363`.
- Reproduced that exact failure against an isolated `git archive HEAD` of the **unchanged** backend: **0 passed, 1 failed** (`.dotnet/unchanged-sync-baseline.log`, `.dotnet/TestResults/unchanged-sync-baseline.trx`). It is not introduced by this patch and remains outside the backup fix; the overall suite must not be reported as passing.
- `git diff --check` completed successfully. Existing line-ending conversion notices are not whitespace errors.

Additional final focused cases verify unknown-length request limits, media-type rejection, source-inconsistency rejection without source mutation, revoked sessions, expired artifacts, bounded ciphertext reads, preservation of legacy capability flags, original review ordering after feed loss, and cancellation after inserted rows with complete transactional rollback. TRX results are in `.dotnet/TestResults/`. Test log/cache artifacts are ignored and are not release source changes.

The initial sandboxed CLI attempt failed accessing its default home directory; a later sandboxed build stalled. Successful executions use workspace-local CLI/package caches, approved execution and single-node compilation with build servers disabled. These are test-runner/environment issues, not backup failures.

## Deployment and remaining limits

Redeploy rebuilt API and Infrastructure assemblies together; existing published binaries are not refreshed by editing source. There are **no new migrations or required new environment variables**. Apply any already-pending migrations using the existing controlled process, including the original backup table/index migrations. Configure and persist the existing `Backups__KeyRingPath`, retention/size/capacity values and normal SQL/JWT settings. Keep the original application name, key ring and Windows service identity/DPAPI recovery material; regenerating keys cannot decrypt old artifacts. Use an absolute protected persistent directory, not an ephemeral container filesystem.

Still unverified: live SQL Server lock/deadlock behavior across replicas, production key persistence/recovery and real-device restore integration. Scheduled expired-artifact cleanup remains operationally managed and expired/failed records still count toward stored capacity. Arbitrary file upload/import and destructive replace/unarchive are deliberately not supported. Format 1 cannot reconstruct missing original review order without either recorded event versions or the old sync feed. Existing NuGet warnings report security advisories for transitive `System.Security.Cryptography.Pkcs` 9.0.11; dependency remediation is outside this focused fix.
