# Lerona final authentication policy — implementation report

Date: 2026-11-10. Existing uncommitted changes were preserved and extended.
This report supersedes the earlier OTP maintenance/activation documents.

## Policy and isolation

Lerona uses six-digit SMS OTP only. Password, email, Google and separate
credential-based activation cannot issue Lerona tokens, including through direct
API requests. Registration happens inside successful OTP verification; new users
have no password hash, email or Google subject. Existing verified-phone users
retain their original IDs, profile values, subscriptions, progress and relationships.

Cent retains its independent authentication scheme, routes, Google/password
services and database. No Cent-owned source, configuration, migrations or schemas
were modified. Its complete regression suite passed 44/44 tests.

## Endpoints

All paths below are under `/api/v1/auth` and retain per-IP authentication limits.
Phone challenges also retain per-phone cooldown, hourly quota and attempt limits.

| Method/path | Result |
| --- | --- |
| POST `/phone/otp/request` | Normalizes Iranian phone; sends six digits; returns challenge/deadlines only. Optional purpose must be `authenticate`. |
| POST `/phone/otp/verify` | Atomically verifies and consumes OTP, opens original verified account or creates a new account, then issues tokens. |
| POST `/refresh` | Rotates only tokens from a durable OTP-authenticated session. |
| POST `/logout`, `/logout/{sessionId}` | Existing owner-bound session revocation. |
| POST `/register`, `/login` | Disabled: HTTP 410 `sms_otp_only`. |
| POST `/google`, `/google/login`, `/google/register` | Disabled: HTTP 410 `sms_otp_only`. |
| POST `/phone/register`, `/phone/register-password`, `/phone/login` | Disabled: HTTP 410 `sms_otp_only`. |
| POST `/phone/activation/request` | Disabled: HTTP 410 `sms_otp_only`. |

Unmapped legacy aliases cannot issue tokens. Disabled routes do not deserialize
credentials or resolve an authentication service. Service compatibility adapters
also reject password/Google requests. Client-supplied verification fields are
never ownership evidence.

OTP hashes use HMAC-SHA256 bound to Lerona, authenticate operation, challenge ID,
canonical phone and code. Codes expire after two minutes by default. Five attempts,
60-second cooldown and five requests/phone/hour remain durable. Failed delivery
cannot be verified. Conditional writes serialize concurrent verifiers and requests;
consumption, account/sync-head creation and session issuance share one transaction.
The unique phone index and challenge lock prevent duplicate account creation and
replay. Database failure rolls back consumption and session/account writes.

The new Lerona-only migration `20261110182603_SmsOtpOnlySessions` adds the durable
`OtpAuthenticated` session marker, revokes historical sessions and consumes obsolete
OTP operations. JWT validation requires `amr=sms_otp` plus a current verified phone
and live marked session. Refresh cannot upgrade an old password/Google session.
The prior verified-phone migration is retained. Neither was applied to production.

## Kavenegar activation

- The actual three SMS settings were moved from `.env.example` into ignored `.env`
  with their values preserved. The example contains placeholders and numeric defaults.
- `.gitignore` already excluded `.env`; the runtime file is ignored and untracked.
- API and read-only checker load `.env` through the same restricted SMS-only loader.
  Host environment settings retain precedence. No Cent/JWT/database/backup setting
  is imported or changed by the loader.
- The real adapter is selected. Placeholder keys/templates and provider debug mode
  cannot authenticate. No mock/simulation fallback exists in the intended live config.
- Configuration check: environment loaded, Kavenegar selected, credentials/template
  present. Read-only provider check did **not** confirm a usable live account.
  An additional read-only HTTPS probe was denied with `EACCES` before reaching
  Kavenegar. This environment's outbound permissions block live validation; the
  result is not evidence that the key is invalid or provider debug mode is enabled.
  Repeat the checker on an authorized host with outbound HTTPS access.
- Verify/Lookup uses the official POST/form contract. Automated tests use isolated
  HTTP handlers to validate approved-template parameter forwarding, accepted delivery
  response shape, permission/provider errors, malformed responses, timeout/cancellation,
  redirects/debug-mode rejection and secret-safe error handling.
- Actual template approval, API permissions and real delivery remain unverified.
  No real SMS has been sent; explicit approval and an Iranian destination are required.
- The exact real key was not found in reachable local Git history in either repo or
  in tracked/unignored source files. Deleted/unavailable/remote-only history was
  not independently audited. Discovery of any historical exposure requires rotation;
  deleting a current file is not history removal. No commit, push or deployment occurred.
- A further scan of 606 local log/report files found no copy of the actual API key.

## Legacy users and rollout gate

Unverified, noncanonical, foreign, landline or conflicting phone ownership requires
recovery. Applicable OTP verification returns `409 account_recovery_required`
after valid OTP, consumes the challenge, issues no tokens and preserves data.
Archived phone reservations cannot create replacements. Unknown phones also cannot
create new accounts while unresolved active legacy accounts remain. This deliberate
signup gate prevents duplicate accounts for email-only/Google-only legacy users.
Existing verified users can continue to log in.

The read-only inventory SQL identifies affected IDs, ownership reasons and
normalization collisions. No live account IDs/counts were available: no host
production Lerona connection string was configured; production appsettings has
none and development contains only a local/example connection. No production
database was opened. Run the inventory with a read-only role and keep its results
in restricted operational storage.

The [setup/recovery guide](KAVENEGAR_OTP_SETUP.md#legacy-account-recovery) proposes
staff-reviewed ownership proof followed by a fresh, account-and-operation-bound SMS
recovery authorization and transactional phone binding to the original ID. That
restricted recovery tool is a proposed follow-up, not an available public endpoint.
No password/Google reauthentication, implicit merging or data overwrites are allowed.
Resolve the inventory before opening signup. Deployment requires explicit approval.

## Validation

| Check | Result |
| --- | --- |
| Full Lerona backend suite, final revision | 309 passed, zero failed; one optional HTTP test skipped in the full run and passed separately below; exit code 0. |
| Full Cent regression suite | 44 passed, zero failed, zero skipped. |
| Additional migration + frontend/backend HTTP regression | 3 backend tests passed, zero skipped/failed; embedded real-HTTP frontend suite passed all 3 flows. |
| Full frontend Jest suite | 123 suites and 1,196 tests passed, zero failures, exit code 0. |
| Frontend TypeScript | Passed after final cleanup. |
| Frontend ESLint | Zero errors; one pre-existing unused `Sm2Scheduler` warning in study tests. |
| Expo prebuild configuration | Passed; no Google sign-in plugin present. |
| Runtime configuration and secret isolation | Passed; real provider preflight not confirmed. |

Backend tests include new signup, original-account login, invalid/expired/reused
and throttled OTPs, phone/operation binding, failed SMS, concurrent one-use
verification/account creation, disabled direct endpoints/client flags, legacy
recovery gates, durable session origin, refresh rotation/reuse/logout and preserved
business data. Frontend tests exercise the sole flow, Persian digits/messages,
validation, timers, expiry, recovery/provider errors, no credential storage before
verification, local account binding and disabled legacy adapters. Google native
integration/dependency/plugin is removed, including both dependency lockfiles.

Builds reported existing NuGet vulnerability-feed connectivity and EF package
version warnings; no dependency/schema change was made to Cent to address them.

Executed validation commands (from the corresponding repository root):

```text
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --no-restore --verbosity minimal
dotnet test backend/Cent.Tests/Cent.Tests.csproj --no-restore --verbosity minimal
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --no-restore --filter "FullyQualifiedName~MobileCloudFlowTests|FullyQualifiedName~PhoneVerificationMigrationTests"
  (with LERONA_FRONTEND_DIR pointing to the local Frontend checkout)
node node_modules/jest/bin/jest.js --runInBand --silent
node node_modules/typescript/bin/tsc --noEmit
node node_modules/eslint/bin/eslint.js .
dotnet run --no-restore --project backend/Lerona.SmsCheck/Lerona.SmsCheck.csproj -- --provider-check
```

Expo prebuild configuration was also generated in memory through the Expo CLI;
only validity/plugin booleans were printed. Read-only secret/history/log audits
printed booleans, counts and filenames only, never actual environment values.

## Task file manifest

Paths are relative to each repository. Pre-existing edits in unrelated screens,
test setup, phone normalization and persistence were retained; this list covers
files changed or added for this task, including extensions of earlier edits.

Backend runtime/policy:

- `.env` (ignored runtime secret file), `.env.example`, `README.md`, `GOOGLE-AUTHENTICATION.md`
- `backend/Flashcards.Api/Program.cs`
- `backend/Flashcards.Api/Endpoints/PhoneAuthEndpoints.cs`, `V1Endpoints.cs`
- `backend/Flashcards.Domain/Entities.cs`
- `backend/Flashcards.Infrastructure/Auth/AuthService.cs`, `GoogleAuthService.cs`,
  `PhoneAuthService.cs`, `PhoneVerification.cs`, `TokenService.cs`, `UserService.cs`,
  `OtpSettings.cs`, `KavenegarOtpSender.cs`, `LeronaSmsEnvironment.cs`
- `backend/Flashcards.Infrastructure/DependencyInjection.cs`
- `backend/Flashcards.Infrastructure/Persistence/Migrations/20261110182603_SmsOtpOnlySessions.cs`,
  `20261110182603_SmsOtpOnlySessions.Designer.cs`, `FlashcardsDbContextModelSnapshot.cs`
- `backend/Lerona.SmsCheck/Program.cs`, `scripts/Start-LeronaLocal.ps1`
- `deploy/20261110182603_SmsOtpOnlySessions.sql`, `deploy/lerona-legacy-phone-inventory.sql`
- `docs/KAVENEGAR_OTP_SETUP.md`, `docs/OTP_SECURITY_ACTIVATION_REPORT.md`,
  `docs/OTP_MAINTENANCE_REPORT.md`, `docs/SMS_OTP_ONLY_REPORT.md`

Backend tests under `backend/Flashcards.Tests/`:

- `AuthTests.cs`, `BackupTests.cs`, `DeckTests.cs`, `GoogleAuthTests.cs`,
  `GuestMigrationTests.cs`, `MobileCloudFlowTests.cs`, `PersistenceTests.cs`,
  `PhoneAuthTests.cs`, `PhoneOtpConcurrencyTests.cs`, `PhoneVerificationSecurityTests.cs`,
  `PhoneVerificationMigrationTests.cs`, `VerifiedRegistration.cs`, `OtpSettingsTests.cs`,
  `LeronaSmsEnvironmentTests.cs`, `SubscriptionTests.cs`, `SyncTests.cs`

Frontend:

- `app.config.js`, `google-app-config.test.ts`, `package.json`, `package-lock.json`, `pnpm-lock.yaml`
- `src/features/auth/presentation/auth-screen.tsx`, `auth-screen.test.tsx`
- `src/core/sync/cloud-client.ts`, `cloud-client.test.ts`, `phone-auth.ts`, `phone-auth.test.ts`,
  `secure-logout.test.ts`, `sync-engine.test.ts`
- Deleted `src/core/sync/google-sign-in.ts` and `google-sign-in.test.ts`
- `src/shared/localization/messages.ts`, `test/cloud-account-flows.e2e.ts`
- `docs/phone-authentication.md`, `docs/KAVENEGAR_OTP_SETUP.md`, `docs/google-authentication.md`

Local test artifacts remain in ignored `Backend/TestResults/` and `Frontend/.tools/`.
No artifact contains the actual API key or real OTP delivery data.

## Complete working-tree status

This snapshot includes pre-existing edits retained from the start of this task.
The ignored runtime `.env` is intentionally absent from Git status. No Cent-owned
file appears in either status list. Generated test logs are ignored.

Backend:

```text
 M GOOGLE-AUTHENTICATION.md
 M README.md
 M backend/Flashcards.Api/Endpoints/PhoneAuthEndpoints.cs
 M backend/Flashcards.Api/Endpoints/V1Endpoints.cs
 M backend/Flashcards.Api/Program.cs
 M backend/Flashcards.Domain/Entities.cs
 M backend/Flashcards.Infrastructure/Auth/AuthService.cs
 M backend/Flashcards.Infrastructure/Auth/GoogleAuthService.cs
 M backend/Flashcards.Infrastructure/Auth/PhoneAuthService.cs
 M backend/Flashcards.Infrastructure/Auth/PhoneNumber.cs
 M backend/Flashcards.Infrastructure/Auth/TokenService.cs
 M backend/Flashcards.Infrastructure/Auth/UserService.cs
 M backend/Flashcards.Infrastructure/DependencyInjection.cs
 M backend/Flashcards.Infrastructure/Persistence/FlashcardsDbContext.cs
 M backend/Flashcards.Infrastructure/Persistence/Migrations/FlashcardsDbContextModelSnapshot.cs
 M backend/Flashcards.Tests/AuthTests.cs
 M backend/Flashcards.Tests/BackupTests.cs
 M backend/Flashcards.Tests/DeckTests.cs
 M backend/Flashcards.Tests/FoundationTests.cs
 M backend/Flashcards.Tests/GoogleAuthTests.cs
 M backend/Flashcards.Tests/GuestMigrationTests.cs
 M backend/Flashcards.Tests/MobileCloudFlowTests.cs
 M backend/Flashcards.Tests/PersistenceTests.cs
 M backend/Flashcards.Tests/PhoneAuthTests.cs
 M backend/Flashcards.Tests/SmsIrOtpSenderTests.cs
 M backend/Flashcards.Tests/SubscriptionTests.cs
 M backend/Flashcards.Tests/SyncTests.cs
?? .env.example
?? backend/Flashcards.Infrastructure/Auth/KavenegarOtpSender.cs
?? backend/Flashcards.Infrastructure/Auth/LeronaSmsEnvironment.cs
?? backend/Flashcards.Infrastructure/Auth/OtpSettings.cs
?? backend/Flashcards.Infrastructure/Auth/PhoneVerification.cs
?? backend/Flashcards.Infrastructure/Persistence/Migrations/20261110145342_RequireVerifiedPhoneAuthentication.Designer.cs
?? backend/Flashcards.Infrastructure/Persistence/Migrations/20261110145342_RequireVerifiedPhoneAuthentication.cs
?? backend/Flashcards.Infrastructure/Persistence/Migrations/20261110182603_SmsOtpOnlySessions.Designer.cs
?? backend/Flashcards.Infrastructure/Persistence/Migrations/20261110182603_SmsOtpOnlySessions.cs
?? backend/Flashcards.Tests/KavenegarOtpSenderTests.cs
?? backend/Flashcards.Tests/LeronaSmsEnvironmentTests.cs
?? backend/Flashcards.Tests/OtpSettingsTests.cs
?? backend/Flashcards.Tests/PhoneOtpConcurrencyTests.cs
?? backend/Flashcards.Tests/PhoneVerificationMigrationTests.cs
?? backend/Flashcards.Tests/PhoneVerificationSecurityTests.cs
?? backend/Flashcards.Tests/VerifiedRegistration.cs
?? backend/Lerona.SmsCheck/Lerona.SmsCheck.csproj
?? backend/Lerona.SmsCheck/Program.cs
?? deploy/20261110145342_RequireVerifiedPhoneAuthentication.sql
?? deploy/20261110182603_SmsOtpOnlySessions.sql
?? deploy/lerona-legacy-phone-inventory.sql
?? docs/KAVENEGAR_OTP_SETUP.md
?? docs/OTP_MAINTENANCE_REPORT.md
?? docs/OTP_SECURITY_ACTIVATION_REPORT.md
?? docs/SMS_OTP_ONLY_REPORT.md
?? scripts/Start-LeronaLocal.ps1
```

Frontend:

```text
 M app.config.js
 M docs/google-authentication.md
 M docs/phone-authentication.md
 M eslint.config.js
 M google-app-config.test.ts
 M jest.config.js
 M package-lock.json
 M package.json
 M pnpm-lock.yaml
 M src/core/sync/cloud-client.test.ts
 M src/core/sync/cloud-client.ts
 D src/core/sync/google-sign-in.test.ts
 D src/core/sync/google-sign-in.ts
 M src/core/sync/phone-auth.test.ts
 M src/core/sync/phone-auth.ts
 M src/core/sync/secure-logout.test.ts
 M src/core/sync/subscription.test.ts
 M src/core/sync/sync-engine.test.ts
 M src/core/sync/sync-engine.ts
 M src/features/auth/presentation/auth-screen.test.tsx
 M src/features/auth/presentation/auth-screen.tsx
 M src/features/study/presentation/study-card.test.tsx
 M src/shared/localization/messages.ts
 M src/shared/navigation/navigation.test.tsx
 M test/cloud-account-flows.e2e.ts
?? docs/KAVENEGAR_OTP_SETUP.md
```
