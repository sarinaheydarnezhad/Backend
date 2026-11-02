Cloud sync audit — 2026-10-27

Reviewed `C:\a\lm` (frontend) and `C:\a\LeronaBackend\Backend` (backend). The configured live API is reachable, but three functional problems were reproduced with isolated test data. No production data, credentials, configuration, or application source was changed.

| Priority | Finding | User impact |
| --- | --- | --- |
| High | New-device defaults are incorrectly queued as a settings edit | An account with existing synced settings can hit a conflict during account entry on a fresh device. |
| High | Initial feeds containing archived records cannot populate fresh local storage | The download transaction fails and the cursor stays at zero. |
| Medium | Local edits do not wake an idle sync engine | Changes remain local during uninterrupted foreground use until another sync trigger occurs. |

1. **New-device defaults conflict with existing account settings.** Actual defaults use `theme: 'light'` in [default-settings.ts](C:/a/lm/src/core/composition/default-settings.ts:7). Opening a new storage scope persists these automatically in [repositories.ts](C:/a/lm/src/core/composition/repositories.ts:31). However, the insert trigger excludes only the old `theme = 'system'` defaults in [migrations.ts](C:/a/lm/src/core/database/migrations.ts:212). Consequently a fresh database already contains a pending settings operation with expected server version zero, without a user edit. The backend correctly rejects it when settings already have a server version. The account-entry preview synchronizes before proceeding in [guest-transfer.ts](C:/a/lm/src/core/sync/guest-transfer.ts:93), and strict synchronization throws on that conflict in [sync-engine.ts](C:/a/lm/src/core/sync/sync-engine.ts:420).

   Evidence: a backend HTTP integration test returned 409 for fresh light defaults against existing dark settings. A frontend test used the actual migrations, SQLite settings repository, defaults, sync store and strict engine path: one unsolicited settings operation appeared, synchronization recorded a conflict and rejected before any pull. This tests the blocking synchronization path; no physical-device login was performed.

   Recommended fix: suppress automatically generated defaults consistently with the current defaults, while continuing to queue explicit user edits. Handle already queued defaults during upgrade without discarding genuine offline edits.

2. **Archived bootstrap records fail on a fresh device.** The backend bootstrap emits a full snapshot with operation `archive` for an already archived deck or card in [SyncBootstrap.cs](C:/a/LeronaBackend/Backend/backend/Flashcards.Infrastructure/Sync/SyncBootstrap.cs:83). The frontend archive handlers only update rows that already exist in [sync-store.ts](C:/a/lm/src/core/sync/sync-store.ts:409). A fresh device has no such row. The following card, review-state or review-event insert then lacks its required parent and fails its foreign key constraint. The transaction rolls back correctly, but subsequent attempts encounter the same feed and cannot advance.

   Evidence: backend HTTP integration tests generated two actual pull responses, one with an archived deck and its card/state, and one with an active deck plus archived card/state. Applying each exact response through the frontend SyncStore against fresh migrated SQLite failed with `FOREIGN KEY constraint failed`; both cursors remained zero. The restore code emits the same archive-only snapshot pattern in [BackupRestoreService.cs](C:/a/LeronaBackend/Backend/backend/Flashcards.Infrastructure/Backups/BackupRestoreService.cs:92); that restore path was inspected, but was not separately reproduced during this audit.

   Recommended fix: materialize missing archived entities from their supplied snapshots, preserving their archived state and parent relationships before applying dependent records, or change initial feed generation to supply a compatible creation sequence. Preserve review history.

3. **Edits made after an idle sync do not schedule another sync.** [sync-engine.ts](C:/a/lm/src/core/sync/sync-engine.ts:490) listens for startup, network changes and app foreground changes. Retry timers handle failures and existing backlogs. Local queue insertions do not notify the engine, and a successful idle pass leaves no periodic timer. A continuously connected app that stays in the foreground can therefore leave newly created decks, edits and reviews pending. Remote changes likewise await another pull trigger.

   Evidence: a test started the real engine with a migrated SQLite store, completed its initial idle pull, then inserted a deck through the real queue trigger. Advancing five minutes produced no push and no additional pull; one operation remained pending. This demonstrates a scheduling gap, not loss of the local record.

   Recommended fix: request a bounded sync after local write transactions commit, preserving the existing concurrency and retry/backoff protections. Define a foreground refresh mechanism if changes from other devices should arrive while this device stays open.

Read-only live checks against `https://lerona-app.com`: `/health` returned 200, `/api/v1/status` returned 200 with `Flashcards API` / `v1`, and unauthenticated `/api/v1/sync/pull?cursor=0` returned 401. These establish reachability and authentication enforcement; they do not establish that authenticated production database synchronization succeeds.

Verification: the existing frontend selection passed **257 tests across 16 suites**. The existing backend sync, guest migration and authentication recovery selection passed **28 tests**. Additional diagnostic tests passed **3 backend cases and 4 frontend cases**; these deliberately assert and demonstrate the current faulty behavior, rather than asserting it has been repaired. Backend diagnostics generated the response fixtures subsequently consumed by frontend diagnostics. All temporary test files were removed from the repositories after copies were saved.

Evidence is in [the audit folder](C:/a/.tools/cloud-sync-audit): [backend diagnostic source](C:/a/.tools/cloud-sync-audit/CloudSyncAudit.cs), [frontend diagnostic source](C:/a/.tools/cloud-sync-audit/cloud-sync-audit.test.ts), [backend execution log](C:/a/.tools/cloud-sync-audit/backend-reproduction.log), [frontend execution log](C:/a/.tools/cloud-sync-audit/frontend-reproduction.log), and the generated `bootstrap-deck.json`, `bootstrap-card.json`, and `default-settings-conflict.json` fixtures. All fixture accounts and content are synthetic.

Commands used for the existing tests:

```powershell
# From C:\a\lm
node node_modules/jest/bin/jest.js --runInBand src/core/sync src/core/composition/sync-session.test.ts src/features/settings/presentation/cloud-sync-screen.test.tsx src/features/settings/presentation/cloud-sync-backup.test.tsx

# From C:\a\LeronaBackend\Backend
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~SyncTests|FullyQualifiedName~GuestMigrationTests|FullyQualifiedName~AuthRecoveryTests' --nologo -v minimal
```

To rerun diagnostics, copy the saved backend diagnostic source into `backend/Flashcards.Tests/CloudSyncAudit.cs` and run the same backend command with filter `FullyQualifiedName~Audit_`. Then copy the saved frontend diagnostic source into `test/cloud-sync-audit.test.ts` and run `node node_modules/jest/bin/jest.js --runInBand --runTestsByPath test/cloud-sync-audit.test.ts`. Run backend diagnostics first because they produce the fixtures. The diagnostic sources intentionally use the current absolute workspace paths.

Existing limitations: cloud sync is disabled on web and requires a native build with an HTTPS API origin. Non-Leitner decks are explicitly rejected by the cloud client, so SM-2 content is not covered by cloud sync. The backend build also emitted existing NuGet vulnerability warnings for `System.Security.Cryptography.Pkcs` 9.0.11 and EF Core 10.0.11/10.0.13 assembly conflict warnings; see the saved build log. Package advisories were not independently assessed in this audit. Production SQL Server locking, authenticated live push/pull, and physical-device behavior remain unverified. No fixes or deployments were performed.
