# Backup implementation verification

Executed October 15, 2026. Backend working directory: `C:\a\LeronaBackend\Backend`. Mobile working directory: `C:\a\lm`. Changed-file manifests and deployment/operational limitations are in [BACKUPS.md](BACKUPS.md) and `lm/docs/server-backups.md`.

## Tooling and backend checks

The system SDK remains 8.0.100. An isolated SDK was installed under `C:\a\.tools\dotnet10` without changing project target frameworks or the machine PATH. The installer selected **10.0.402**, with ASP.NET Core runtime **10.0.13**. Initial SDK startup also installed its development HTTPS certificate; it was not trusted or used to expose an API. EF tooling **10.0.11** was installed under `C:\a\.tools\ef10`.

Executed bootstrap commands:

```powershell
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile 'C:\a\.tools\dotnet-install.ps1'
& 'C:\a\.tools\dotnet-install.ps1' -Channel 10.0 -InstallDir C:\a\.tools\dotnet10 -NoPath
dotnet tool install dotnet-ef --tool-path C:\a\.tools\ef10 --version 10.0.11
```

Subsequent backend commands used this process-local prologue:

```powershell
$env:DOTNET_ROOT='C:\a\.tools\dotnet10'
$env:PATH="$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
```

### Final build and focused tests

```powershell
dotnet build backend/Flashcards.slnx --nologo -v minimal 2>&1 | Select-Object -Last 5; exit $LASTEXITCODE
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --filter 'FullyQualifiedName~BackupTests|FullyQualifiedName~PersistenceTests|FullyQualifiedName~AuthTests' --nologo -v minimal 2>&1 | Select-Object -Last 8; exit $LASTEXITCODE
```

Both exit **0**. The solution builds with zero errors and 16 warning occurrences; **40 focused tests pass**, including **12 backup cases** and existing authentication/persistence regressions. The final focused run completes in seven seconds. Backup cases exercise encrypted owned contents, archived data/relationships/counters/dates, credential/foreign-data exclusion, active JWT requirements, all ownership reads, user-ID spoofing prevention, same-request retries, conflicting parameters, UTC expiration, no restore route, failed-save rollback, cancellation, format rejection, size limits, missing durable-key configuration, minimum sync version, pagination/capacity and payload/size completion constraints. Purpose binding and ciphertext tampering are checked through Data Protection.

Tests use SQLite and an ephemeral Data Protection provider, not a live SQL Server or production key ring. The backup fixture converts only backup UTC columns to sortable ticks for SQLite; production mappings stay SQL Server `datetimeoffset(7)`. Real key persistence/Windows DPAPI recovery and SQL Server concurrency still require operational verification.

Warnings include pre-existing moderate/high NuGet advisories for transitive `System.Security.Cryptography.Pkcs 9.0.11` through EF SqlServer → Microsoft.Data.SqlClient, plus NU1510 redundant-package warnings after the ASP.NET framework reference. These were not suppressed or represented as fixed. The framework reference avoids introducing a separate NuGet Data Protection cryptography dependency tree.

Earlier development runs found two failure-metadata tracking cases; those were fixed by preventing re-attachment of an already existing owner graph. Model/entity-count assertions were updated for the new table/migration. The final focused command includes those fixes and passes.

### Full backend suite and baseline comparison

```powershell
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --nologo -v minimal 2>&1 | Select-Object -Last 35; exit $LASTEXITCODE
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --nologo -v minimal 2>&1 | Select-Object -Last 25; exit $LASTEXITCODE
```

Both exit **1**: **95 passed, 1 failed, 96 total**. The remaining failure is `SyncTests.Guest_copy_retries_preserve_distinct_cards_and_historical_progress_with_owner_only_reads`, an HTTP 500 at `SyncTests.cs:363`. The final full run includes the nullable-size constraint strengthening and completes in 16 seconds.

An untouched HEAD archive was extracted to a separate tools directory and checked:

```powershell
git archive --format=zip --output=C:\a\.tools\backend-before-backups.zip HEAD backend
Expand-Archive -LiteralPath C:\a\.tools\backend-before-backups.zip -DestinationPath C:\a\.tools\backend-before-backups
dotnet test C:\a\.tools\backend-before-backups\backend\Flashcards.Tests\Flashcards.Tests.csproj --filter FullyQualifiedName~Guest_copy_retries_preserve_distinct_cards_and_historical_progress_with_owner_only_reads --nologo -v minimal 2>&1 | Select-Object -Last 23; exit $LASTEXITCODE
```

The isolated baseline test also exits **1**, with the same HTTP 500 at the same line. That unrelated failure was left unchanged; the full suite is not described as passing.

### SQL Server model and migration

```powershell
& 'C:\a\.tools\ef10\dotnet-ef.exe' migrations has-pending-model-changes --project backend/Flashcards.Infrastructure --startup-project backend/Flashcards.Infrastructure --no-color 2>&1 | Select-Object -Last 3; exit $LASTEXITCODE
& 'C:\a\.tools\ef10\dotnet-ef.exe' migrations script 20261012120000_AddPhoneAuthentication 20261015091415_AddUserBackups --idempotent --project backend/Flashcards.Infrastructure --startup-project backend/Flashcards.Infrastructure --no-color 2>&1 | Select-Object -Last 60; exit $LASTEXITCODE
```

Both exit **0**: no pending model changes; SQL Server idempotent SQL generated for only the new backup table, constraints, owner foreign key and indexes. Script generation occurred before the final additional `SizeBytes IS NOT NULL` guard; the final model-match and focused SQL-generation/constraint tests were rerun after that guard.

Migration scaffolding was done in an isolated source copy under `C:\a\.tools\backup-schema-stage`, then applied to source through patches. The first tool attempt lacked restored assets; the second, with API startup, failed because that project lacks a direct EF Design reference. After restore, Infrastructure startup succeeded using the existing design-time factory:

```powershell
dotnet restore C:\a\.tools\backup-schema-stage\Flashcards.Api\Flashcards.Api.csproj --nologo -v quiet
& 'C:\a\.tools\ef10\dotnet-ef.exe' migrations add AddUserBackups --project C:\a\.tools\backup-schema-stage\Flashcards.Infrastructure --startup-project C:\a\.tools\backup-schema-stage\Flashcards.Infrastructure --context FlashcardsDbContext --no-color
```

No live database update, cleanup SQL, deployment or restore was executed.

## Mobile checks

### Final focused tests

```powershell
node node_modules/jest/bin/jest.js --runInBand src/core/sync/server-backup.test.ts src/core/sync/secure-logout.test.ts src/core/sync/logout-journal.test.ts src/features/settings/presentation/logout-screen.test.tsx src/core/sync/cloud-client.test.ts src/core/sync/sync-engine.test.ts src/core/sync/guest-transfer.test.ts src/core/composition/application-scopes.test.ts src/core/composition/sync-session.test.ts src/core/storage/guest-free-tier.test.ts src/shared/bootstrap/data-scope-boundary.test.tsx src/features/settings/presentation/settings-account-card.test.tsx test/architecture.test.ts 2>&1 | Select-Object -Last 24; exit $LASTEXITCODE
```

Exit **0**: **13 suites, 122 tests pass**, 18.948 seconds. Coverage includes actual SQLite guest/account isolation, confirmed retained metadata, backup failure without logout, interrupted POST after simulated commit, durable retry after client restart, single-artifact verification retry, malformed/expired/incompatible metadata, no unintended guest uploads, UTC-retention UI and expired receipts. HTTP, native SecureStore/navigation and the server artifact repository are mocked; these are not live end-to-end/device tests.

### Types, scoped lint and formatting

```powershell
node node_modules/typescript/bin/tsc --noEmit; exit $LASTEXITCODE
node node_modules/eslint/bin/eslint.js src/core/sync/cloud-client.ts src/core/sync/logout-journal.ts src/core/sync/server-backup.ts src/core/sync/server-backup.test.ts src/core/sync/secure-logout.test.ts src/core/sync/sync-engine.ts src/features/auth/presentation/logout-notice.tsx src/features/settings/presentation/logout-screen.test.tsx src/shared/localization/messages.ts; exit $LASTEXITCODE
node node_modules/prettier/bin/prettier.cjs --check src/core/sync/cloud-client.ts src/core/sync/logout-journal.ts src/core/sync/server-backup.ts src/core/sync/server-backup.test.ts src/core/sync/secure-logout.test.ts src/core/sync/sync-engine.ts src/features/auth/presentation/logout-notice.tsx src/features/settings/presentation/logout-screen.test.tsx src/shared/localization/messages.ts README.md docs/secure-logout.md docs/server-backups.md; exit $LASTEXITCODE
```

All exit **0**, with no TypeScript or scoped lint diagnostics and matching Prettier style. A render-time clock lint violation found during development was fixed with state/effect-based expiration updates and checked again. Code formatting was applied through patches, not unrestricted file rewrites.

### Full mobile suite

```powershell
node node_modules/jest/bin/jest.js --runInBand 2>&1 | Select-String -Pattern '^(PASS|FAIL)|^  ●|^Test Suites:|^Tests:|^Snapshots:|^Time:|^Ran all'; exit $LASTEXITCODE
```

Exit **1**: **87 suites passed, 3 failed; 705 tests passed, 6 failed, 711 total**, 137.922 seconds. These are the previously documented language/RTL failures in `navigation.test.tsx` (3), `study-card.test.tsx` (2) and `create-settings-use-cases.test.ts` (1), not changed by this task. This run precedes the final clock-effect change and one expired-receipt UI test; the final focused command above was rerun afterward.

### Android bundle export

```powershell
node node_modules/expo/bin/cli export --platform android --output-dir .tools/server-backup-export 2>&1 | Select-Object -Last 10; exit $LASTEXITCODE
```

Exit **0** on the final run: one 8.1 MB Hermes bundle `entry-9d415c644ed5646aabc10ea3bd1b2559.hbc`, assets and metadata exported. This is not an APK/native compilation, physical-device test, iOS build, real OTP delivery or live backup integration.

## Remaining operational work

Configure a durable protected key ring, existing JWT/SQL settings and a phone-accessible HTTPS API URL. Apply the migration with the existing deployment process. Verify SQL Server concurrent pushes/backups, encrypted-key persistence/recovery, real-device flow and storage-failure behavior. Configure and monitor artifact-only expired-backup cleanup through existing operational scheduling if physical deletion is required; no automatic cleanup was installed. Resolve the reported transitive dependency advisories before production deployment. Restore needs a separate conflict/version/immutable-history design and remains unavailable.

Both repositories also ran `git diff --check`; no whitespace errors were reported (only normal LF/CRLF warnings). Pre-existing launch-profile changes remain intact.
