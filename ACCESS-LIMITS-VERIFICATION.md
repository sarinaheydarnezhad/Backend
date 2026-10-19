# Access-limit verification

Executed October 15, 2026. Backend cwd: `C:\a\LeronaBackend\Backend`; mobile cwd: `C:\a\lm`. See ACCESS-LIMITS.md for the operation-by-operation enforcement matrix, complete changed-file manifest and remaining risks. Earlier backup/auth/subscription changes in the worktrees were preserved; no deployment, SQL Server update, payment call or secret/configuration change was performed.

## Backend

The .NET 10 project was tested/built using the already-installed isolated SDK 10.0.402, not the system SDK 8.0.100. All backend commands used this process-local prologue:

```powershell
$env:DOTNET_ROOT='C:\a\.tools\dotnet10'
$env:PATH="$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
```

```powershell
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --filter 'FullyQualifiedName~SubscriptionTests|FullyQualifiedName~PersistenceTests|FullyQualifiedName~AuthTests|FullyQualifiedName~BackupTests' --nologo -v minimal 2>&1 | Select-Object -Last 7; exit $LASTEXITCODE
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --nologo -v minimal 2>&1 | Select-Object -Last 24; exit $LASTEXITCODE
dotnet build backend/Flashcards.slnx --nologo -v minimal 2>&1 | Select-Object -Last 6; exit $LASTEXITCODE
```

- Focused suite: **exit 0; 70 passed**, including 30 subscription/access cases; 19 seconds.
- Full suite: **exit 1; 125 passed, 1 failed, 126 total**, 27 seconds. The same pre-existing failure remains: SyncTests.Guest_copy_retries_preserve_distinct_cards_and_historical_progress_with_owner_only_reads returns HTTP 500 at SyncTests.cs:363 under SQLite. It was already recorded before this task in SUBSCRIPTION-VERIFICATION.md/BACKUP-VERIFICATION.md. Full success is not claimed.
- Build: **exit 0; zero errors, 16 warning occurrences**, 4.53 seconds. Existing NU1902/NU1903 advisories on transitive System.Security.Cryptography.Pkcs 9.0.11 and NU1510 warnings remain; no package was added/upgraded. Resolve advisories before production deployment.

The new backend tests execute real in-process authenticated HTTP routes against relational SQLite with the existing test clock. They cover study preflight and both review mutation types at 1 deck/50 cards, deck/card excess and active Plus; exact expiry blocks new reviews, old accepted receipts remain idempotent, rejected progress/feed changes do not persist; cross-dimension growth, cross-account isolation, unchanged view/pull access, explicit archiving to restore free study and atomic rejection of a 50-change push taking the account to 51 cards. Existing subscription tests retain Full/future/cancelled/UTC-boundary coverage.

Earlier diagnostic commands also ran the filters FullyQualifiedName~SubscriptionTests|FullyQualifiedName~SyncTests (Last 25) and FullyQualifiedName~SubscriptionTests (Last 28/40). The former retained the known unrelated failure; the final subscription-only run passed 30/30. Two initial new-test assertions incorrectly expected a 51-change batch to pass existing request validation and an archived deck to appear in active-only reads; those fixtures/assertions were corrected, not the validators/queries. There is no new SQL Server schema migration in this task and none was applied.

## Mobile focused tests

```powershell
node node_modules/jest/bin/jest.js --runInBand src/core/access/learning-access.test.ts src/core/composition/learning-access.test.ts src/shared/access/learning-access-notice.test.tsx src/core/sync/subscription.test.ts src/core/sync/cloud-client.test.ts src/core/sync/sync-engine.test.ts src/core/sync/guest-transfer.test.ts src/core/sync/secure-logout.test.ts src/core/storage/guest-free-tier.test.ts src/core/composition/application-scopes.test.ts src/core/composition/sync-session.test.ts src/features/transfer/application/transfer-service.test.ts src/features/transfer/data/adapters.test.ts src/features/study/application/study-queue.test.ts src/features/study/application/study-session-flow.test.ts src/features/study/application/scheduler-selection.test.ts src/features/study/data/scheduler-persistence.test.ts src/core/application/create-application.test.ts src/core/sync/server-backup.test.ts src/core/sync/logout-journal.test.ts src/features/settings/presentation/settings-account-card.test.tsx src/features/settings/presentation/logout-screen.test.tsx src/features/auth/presentation/guest-data-screen.test.tsx src/shared/bootstrap/data-scope-boundary.test.tsx test/architecture.test.ts 2>&1 | Select-Object -Last 32; exit $LASTEXITCODE
```

Final run: **exit 0; 25 suites, 304 tests passed**, 30.395 seconds. Earlier focused runs passed 302/303 tests before the final latency/cache-writer regression cases were added; only the final count is used for completion claims.

Coverage includes exact guest limits/offline study, server versus local usage, real SQLite Plus creation above free quotas and study, expiry/unknown fallback, preservation/edit/export, import preflight with no quota-driven partial writes, every application study entry, direct SQLite review/session guards, bounded offline cache/restart, stale/equal response replay and concurrent cache writers, request latency and clock rollback, revocation during fetch/cache restoration, 401/403 handling, early sign-in credential binding, cross-account/late-response isolation, shared notice/plan navigation, additive confirmed Plus guest transfer and existing logout/backup/sync regressions. HTTP, SecureStore and network/provider behavior are mocked; SQLite is real SQL.js in the mobile tests. No physical device was exercised.

Initial full/focused iterations exposed test fixtures that intentionally assumed unrestricted multi-deck access and a guest notice unnecessarily registering native network listeners. The guest notice now makes no network calls/listeners, and unrelated scheduler/import/navigation unit fixtures explicitly inject permitted access. Dedicated new tests cover actual authorization; no production test-mode entitlement bypass was added. One pre-existing fetch mock needed an explicit RequestInfo | URL parameter annotation to match the project's fetch overloads. These iterations are not claimed passing.

## Full mobile suite

```powershell
node node_modules/jest/bin/jest.js --runInBand 2>&1 | Select-String -Pattern '^FAIL|^  ●|^Test Suites:|^Tests:|^Time:|^Ran all'; exit $LASTEXITCODE
```

Final result: **exit 1; 91 suites passed, 3 failed, 94 total; 800 tests passed, 6 failed, 806 total**, 82.91 seconds. The same known language/RTL failures remain:

- Navigation: app-language/RTL labels, deck-form field errors, card-form field errors (3).
- Study-card RTL alignment/typography and answer-face alignment (2).
- Settings UI-language/reminder independence (1).

The existing navigation fixture was adapted for explicitly permitted multi-deck test data; those three baseline assertions were not fixed or suppressed. The full suite is not claimed passing. Prior full commands used the same filter including ^PASS and an earlier final-state run had 799 passes/6 failures before the additional cache regression test. Other exploratory selections ran the affected scheduler/import/navigation suites, the new policy/composition/UI suites and guest-transfer regressions.

## Build, types and style

```powershell
node node_modules/typescript/bin/tsc --noEmit; exit $LASTEXITCODE
node node_modules/eslint/bin/eslint.js src/core/ports/learning-access.ts src/core/access/learning-access.ts src/core/access/learning-access.test.ts src/core/composition/learning-access.ts src/core/database/entitlement-cache.ts src/core/database/free-tier-guards.ts src/core/database/database-error.ts src/core/composition/application.ts src/core/composition/sync.ts src/core/composition/transfer.ts src/core/application/create-application.ts src/core/application/create-application.test.ts src/core/sync/cloud-client.ts src/core/sync/cloud-client.test.ts src/core/sync/secure-logout.test.ts src/core/sync/guest-transfer.ts src/core/sync/guest-transfer.test.ts src/core/storage/guest-free-tier.test.ts src/shared/access/learning-access-notice.tsx src/shared/access/learning-access-notice.test.tsx src/features/settings/presentation/subscription-screen.tsx src/features/settings/presentation/settings-account-card.tsx src/features/decks/presentation/deck-editor-screen.tsx src/features/decks/presentation/use-cards-view-model.ts src/features/study/application/create-study-use-cases.ts src/features/study/application/study-queue.test.ts src/features/study/application/study-session-flow.test.ts src/features/study/application/scheduler-selection.test.ts src/features/study/data/scheduler-persistence.test.ts src/features/transfer/application/transfer-service.ts src/features/transfer/application/transfer-service.test.ts src/features/transfer/data/adapters.test.ts src/shared/navigation/navigation.test.tsx src/shared/localization/messages.ts src/app/_layout.tsx src/app/settings/subscription.tsx src/core/composition/learning-access.test.ts src/core/sync/subscription.test.ts; exit $LASTEXITCODE
node node_modules/prettier/bin/prettier.cjs --check src/core/ports/learning-access.ts src/core/access/learning-access.ts src/core/access/learning-access.test.ts src/core/composition/learning-access.ts src/core/database/entitlement-cache.ts src/core/database/free-tier-guards.ts src/core/database/database-error.ts src/core/composition/application.ts src/core/composition/sync.ts src/core/composition/transfer.ts src/core/application/create-application.ts src/core/application/create-application.test.ts src/core/sync/cloud-client.ts src/core/sync/cloud-client.test.ts src/core/sync/secure-logout.test.ts src/core/sync/guest-transfer.ts src/core/sync/guest-transfer.test.ts src/core/storage/guest-free-tier.test.ts src/shared/access/learning-access-notice.tsx src/shared/access/learning-access-notice.test.tsx src/features/settings/presentation/subscription-screen.tsx src/features/settings/presentation/settings-account-card.tsx src/features/decks/presentation/deck-editor-screen.tsx src/features/decks/presentation/use-cards-view-model.ts src/features/study/application/create-study-use-cases.ts src/features/study/application/study-queue.test.ts src/features/study/application/study-session-flow.test.ts src/features/study/application/scheduler-selection.test.ts src/features/study/data/scheduler-persistence.test.ts src/features/transfer/application/transfer-service.ts src/features/transfer/application/transfer-service.test.ts src/features/transfer/data/adapters.test.ts src/shared/navigation/navigation.test.tsx src/shared/localization/messages.ts src/app/_layout.tsx src/app/settings/subscription.tsx src/core/composition/learning-access.test.ts src/core/sync/subscription.test.ts docs/subscriptions.md docs/access-limits.md ../LeronaBackend/Backend/SUBSCRIPTIONS.md ../LeronaBackend/Backend/ACCESS-LIMITS.md; exit $LASTEXITCODE
node node_modules/expo/bin/cli export --platform android --output-dir .tools/access-limits-export 2>&1 | Select-Object -Last 12; exit $LASTEXITCODE
```

- Final TypeScript, scoped ESLint and Prettier checks: **exit 0**. Formatting used in-memory Prettier output applied as patches; mixed pre-existing CRLF/LF sections in touched files were normalized through patches and rechecked. No formatter/configuration/package was introduced.
- Final Android JavaScript export: **exit 0**, one 8.1 MB Hermes bundle entry-6ba1a3ec22747e410b51bc87b6a0b46f.hbc plus assets/metadata. Earlier exports also passed. This is **not** a native APK/Gradle/EAS build, iOS build or physical-device validation.
- Both repositories executed git diff --check; exit $LASTEXITCODE: **exit 0**, only ordinary LF/CRLF notices and no whitespace errors. Documentation-only additions are checked again before completion.

## Operational limitations

Use existing SQL/JWT/native HTTPS configuration and Task 7's subscription migration; no new secret/environment variable is required. Full, payments and real checkout remain unavailable. The read-only plan-information destination is not a designed Paywall. Offline permissions are explicitly provisional for at most five minutes and never exempt server mutations from fresh authorization. A modified device can alter local UX/storage/clock; it cannot activate a server subscription. Live SQL Server quota concurrency, deployment, native-device behavior, a future provider and archived-data management UI are not verified/implemented here.
