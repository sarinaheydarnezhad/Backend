# Subscription implementation verification

Executed October 15, 2026. Backend cwd: `C:\a\LeronaBackend\Backend`; mobile cwd: `C:\a\lm`. Changed-file manifest, API contracts, product assumptions and limitations are in `SUBSCRIPTIONS.md` and `lm/docs/subscriptions.md`.

No payment provider, purchase endpoint, Paywall, deployment, live migration application or production activation was performed. Existing authentication, backup/logout work and the pre-existing launch-profile change were preserved.

## Backend tooling and build

The project remains .NET 10 / EF Core 10. The system SDK remains 8.0.100; commands used the previously installed isolated SDK **10.0.402** (ASP.NET runtime **10.0.13**) and EF tool **10.0.11**, without changing global PATH or project targets.

Process-local prologue for all backend commands:

```powershell
$env:DOTNET_ROOT='C:\a\.tools\dotnet10'
$env:PATH="$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
```

```powershell
dotnet build backend/Flashcards.slnx --nologo -v minimal 2>&1 | Select-Object -Last 5; exit $LASTEXITCODE
```

Final build **exit 0, zero errors, 16 warning occurrences**, 3.57 seconds. An initial build also passed with `Select-Object -Last 17`. Warnings include existing NU1902/NU1903 advisories on transitive `System.Security.Cryptography.Pkcs 9.0.11` and NU1510 redundant references; no new package was added or advisory claimed fixed. Resolve security advisories before production deployment.

## Backend tests

```powershell
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --filter 'FullyQualifiedName~SubscriptionTests|FullyQualifiedName~PersistenceTests|FullyQualifiedName~AuthTests|FullyQualifiedName~BackupTests' --nologo -v minimal 2>&1 | Select-Object -Last 8; exit $LASTEXITCODE
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --nologo -v minimal 2>&1 | Select-Object -Last 28; exit $LASTEXITCODE
```

Final focused run **exit 0, 64 passed**, including **24 subscription cases** plus existing persistence/authentication/backup regressions. Tests cover public centralized prices/catalog, unavailable Full/purchases, active and exact-start access, future starts, explicit cancellation/expiration, stale Active expiry at the exact tick, ownership/query spoofing, revoked JWT sessions, repeated read-only requests, absent activation routes, generic-sync activation rejection, UTC normalization, invalid plan/status/period constraints, duplicate Active/future/expired-stored-Active rows, atomic retirement/insertion, exact free usage at 1 deck/50 cards, foreign/archived exclusion, real deck/card write enforcement, Plus growth beyond 1/50, expiration blocking further growth while preserving existing data and edits.

Full suite **exit 1, 119 passed, 1 failed, 120 total**. The failure remains `SyncTests.Guest_copy_retries_preserve_distinct_cards_and_historical_progress_with_owner_only_reads`, HTTP 500 at `SyncTests.cs:363`, previously reproduced on untouched HEAD during the backup task (see `BACKUP-VERIFICATION.md`). This unrelated test/file was not changed. The full suite is not claimed passing.

Earlier narrow test commands used the filter `FullyQualifiedName~SubscriptionTests|FullyQualifiedName~PersistenceTests` and `Select-Object -Last 65` / `-Last 60`. The first failed compilation because a test referenced internal SyncValidator options; the test now uses standard web JSON options. The next run passed 28/29 with one new SQL-script assertion failure caused by idempotent SQL quote escaping. That assertion was corrected to the actual escaped unique-index script. Final focused runs include these fixes, forged-sync rejection and exact-boundary usage checks.

Tests use SQLite with tick conversion for **only subscription timestamps** in the new fixture so SQL-style date ordering and tick boundaries can be exercised. Production columns remain SQL Server `datetimeoffset(7)`. No live SQL Server connection, production concurrency race or provider billing lifecycle was verified.

## Migration verification

Scaffolding used a minimal isolated source stage under `C:\a\.tools\subscription-schema-stage` (Domain/Application plus Infrastructure project, DbContext/factory and existing snapshot), then migration files and only new snapshot blocks were applied through patches to the real source. An initial scaffold attempt failed with missing restored assets; after restore the following succeeded:

```powershell
dotnet restore C:\a\.tools\subscription-schema-stage\Flashcards.Infrastructure --nologo -v quiet 2>&1 | Select-Object -Last 5
& 'C:\a\.tools\ef10\dotnet-ef.exe' migrations add AddSubscriptions --project C:\a\.tools\subscription-schema-stage\Flashcards.Infrastructure --startup-project C:\a\.tools\subscription-schema-stage\Flashcards.Infrastructure --context FlashcardsDbContext --no-color 2>&1 | Select-Object -Last 12
& 'C:\a\.tools\ef10\dotnet-ef.exe' migrations has-pending-model-changes --project backend/Flashcards.Infrastructure --startup-project backend/Flashcards.Infrastructure --no-color 2>&1 | Select-Object -Last 5; exit $LASTEXITCODE
& 'C:\a\.tools\ef10\dotnet-ef.exe' migrations script 20261015091415_AddUserBackups 20261015123452_AddSubscriptions --idempotent --project backend/Flashcards.Infrastructure --startup-project backend/Flashcards.Infrastructure --no-color 2>&1 | Select-Object -Last 65; exit $LASTEXITCODE
```

After restore, scaffolding **exit 0**. Real-source model check **exit 0, no pending model changes**. Idempotent script generation **exit 0**; inspected SQL creates only Subscriptions, its plan/status/period constraints, NoAction user FK, owner/history index and filtered unique Active index, then records its migration ID. **No database update was executed.**

## Mobile checks

```powershell
node node_modules/jest/bin/jest.js --runInBand src/core/sync/subscription.test.ts src/core/sync/server-backup.test.ts src/core/sync/secure-logout.test.ts src/core/sync/cloud-client.test.ts src/core/sync/sync-engine.test.ts src/core/sync/guest-transfer.test.ts src/core/sync/logout-journal.test.ts src/features/settings/presentation/settings-account-card.test.tsx src/features/auth/presentation/guest-data-screen.test.tsx src/features/settings/presentation/logout-screen.test.tsx src/core/composition/application-scopes.test.ts src/core/composition/sync-session.test.ts src/core/storage/guest-free-tier.test.ts src/shared/bootstrap/data-scope-boundary.test.tsx test/architecture.test.ts 2>&1 | Select-Object -Last 25; exit $LASTEXITCODE
node node_modules/typescript/bin/tsc --noEmit; exit $LASTEXITCODE
node node_modules/eslint/bin/eslint.js src/core/sync/subscription.ts src/core/sync/subscription.test.ts src/core/sync/cloud-client.ts src/core/sync/sync-engine.ts; exit $LASTEXITCODE
node node_modules/prettier/bin/prettier.cjs --check src/core/sync/subscription.ts src/core/sync/subscription.test.ts src/core/sync/cloud-client.ts src/core/sync/sync-engine.ts docs/subscriptions.md ../LeronaBackend/Backend/SUBSCRIPTIONS.md ../LeronaBackend/Backend/SUBSCRIPTION-VERIFICATION.md; exit $LASTEXITCODE
```

Focused tests **exit 0, 15 suites/198 tests passed**, 23.073 seconds. An earlier six-suite selection (subscription, cloud-client, sync-engine, secure-logout, guest-free-tier, architecture; `Select-Object -Last 42`) also passed **100 tests**. Coverage includes actual response DTOs, server price preservation, Full disabled, malformed/expired/future responses, authenticated owner-free request contracts, guest access restrictions, network failures, expired-session credential clearing and rejection of late subscription responses after logout. Existing isolation, backup and migration regressions remain covered. HTTP/native SecureStore/provider responses are mocked; no real purchase or physical-device access was tested.

Final typecheck, scoped ESLint and formatting **exit 0**. One initial TypeScript implicit-any diagnostic in the catalog validator was fixed; formatting was patched and rechecked after one remaining test-file style warning. No formatter or package was introduced.

```powershell
node node_modules/jest/bin/jest.js --runInBand 2>&1 | Select-String -Pattern '^(PASS|FAIL)|^  ●|^Test Suites:|^Tests:|^Snapshots:|^Time:|^Ran all'; exit $LASTEXITCODE
```

Full mobile suite **exit 1; 88 suites passed, 3 failed; 775 tests passed, 6 failed, 781 total**, 67.82 seconds. The same known language/RTL failures remain in navigation (3), study-card (2) and create-settings-use-cases (1). Those files were not modified; the full suite is not claimed passing.

```powershell
node node_modules/expo/bin/cli export --platform android --output-dir .tools/subscription-model-export 2>&1 | Select-Object -Last 12; exit $LASTEXITCODE
```

Export **exit 0**: one 8.1 MB Hermes bundle `entry-3974f91dd3e0b6fe55c3d78493a81691.hbc`, assets and metadata. This is an Android JavaScript export, **not** a native APK/Gradle build or iOS/device verification.

Both repositories ran `git diff --check; exit $LASTEXITCODE`; no whitespace errors (only normal LF/CRLF warnings).

## Unimplemented and operational dependencies

Apply the migration through the existing SQL deployment process; retain existing SQL/JWT/mobile HTTPS configuration and use a .NET 10 runtime. No new subscription secret/configuration is required for these read APIs. A verified purchase provider, idempotent transactional subscription writer, reconciliation/renewal/cancellation business workflows and provider credentials are **not implemented or configured**. Full remains unavailable. Local authenticated SQLite creation and guest-copy previews remain conservatively free-tier-limited; no offline paid-entitlement cache/unlock or Paywall is claimed. Live SQL Server application/concurrency, native-device reads and real payments remain unverified.
