> Historical report. Superseded by [password login with SMS-verified signup/recovery](PHONE_PASSWORD_OTP_REPORT.md). Do not deploy the old SMS-only/session-revocation policy.

> Historical document. Superseded by [the SMS OTP-only policy](SMS_OTP_ONLY_REPORT.md). Lerona password/Google/activation routes now return 410; Cent is unchanged.

# Lerona OTP maintenance report — 2026-11-10

> Historical preparation report. The subsequent mandatory SMS security update
> disables unverified registration and legacy session access, adds a Lerona-only
> migration and prepares `.env.kavenegar.local`. Current behavior and activation
> instructions are in [KAVENEGAR_OTP_SETUP.md](KAVENEGAR_OTP_SETUP.md); current
> verification results are in [OTP_SECURITY_ACTIVATION_REPORT.md](OTP_SECURITY_ACTIVATION_REPORT.md).

## Scope and audit

Inspected the two repositories, .NET project references, Expo dependencies,
phone/email/Google authentication, database mappings, API validation/rate limits,
token rotation/revocation, native credential persistence, account binding, auth
screen state and navigation. No production services or databases were contacted.
Kavenegar's official REST documentation was checked; provider tests use a fake HTTP
handler, not a live SMS account.

## Confirmed bugs and fixes

| Problem and root cause | Changed files | Fix | Verification |
| --- | --- | --- | --- |
| Backend rejected local Iranian numbers; frontend local validation also accepted landlines because it checked only a leading `0` | `Auth/PhoneNumber.cs`, frontend `core/sync/phone-auth.ts` | Canonicalize local `09…` into `+989…`; require Iranian mobile identity for OTP; accept international Iranian input in the existing form; preserve legacy international password accounts | Normalization cases, equivalent-format cooldown and duplicate-account integration cases; frontend normalization tests |
| Auth requests discarded `Retry-After`, so the screen could not honor a server throttle accurately | `Endpoints/PhoneAuthEndpoints.cs`, `Auth/PhoneAuthService.cs`, frontend `core/sync/cloud-client.ts` | Return precise phone cooldown/hourly quota delay and preserve the response header in `CloudError.retryAfterMs` | Backend cooldown/hourly limit assertions; frontend client and countdown tests |
| OTP adapters existed in the client but the screen exposed no OTP request/verify/resend flow | Frontend `features/auth/presentation/auth-screen.tsx`, `shared/localization/messages.ts` | Restore explicit OTP login and verified signup using existing components/session methods; add expiry, resend, pending and error states | Auth screen tests cover login, signup, missing provider, invalid code, expiry/resend, network and rate limits |
| `npm run lint` crashed with `EPERM` while scanning `.secrets` | Frontend `eslint.config.js`, `jest.config.js` | Exclude protected secrets and local package-cache directories from discovery | Initial failure reproduced; final full lint passed with one existing study-test warning |
| Broad navigation fixtures assumed insertion order although sessions now default to random; synthetic IDs also sorted incorrectly across decimal boundaries and the route map omitted existing guest-data/logout routes | Frontend `shared/navigation/navigation.test.tsx` | Keep workflow fixture order deterministic, zero-pad fixture IDs, and register the real existing routes; production study behavior is unchanged | Six focused authentication-route/study-order checks passed; broader failures remain below |

File prefixes above are `backend/Flashcards.Infrastructure/` for backend `Auth/`,
`backend/Flashcards.Api/` for `Endpoints/`, and `Frontend/src/` for frontend source.
No theme, visual tokens, marketing content, layout components or animations changed.

## Authentication additions

- `OtpSettings.cs`: validated configurable 6–8 digits, 120-second default expiry,
  60-second cooldown, five verification attempts and five phone requests/hour.
- `KavenegarOtpSender.cs`: existing `ISmsOtpSender` boundary, official HTTPS lookup
  form, configured key/template, timeout, redirect rejection, structured response
  validation and sanitized provider errors. Built-in HTTP logging is removed only
  for this sender because its URL contains the key.
- Existing keyed hashes, constant-time comparison, transactional verification,
  replay prevention, registration proof consumption, unique phone index and JWT
  session mechanism are reused. No schema or migration change was necessary.
- Runtime mock providers are unavailable. Capture senders are private test types
  in `Flashcards.Tests`; configuration cannot select them in any environment.
- Existing password-only registration remains available and unverified. OTP login
  never auto-registers, links or overwrites accounts. Verified signup keeps its
  existing username/password rules.

## Protected project / shared files

No `SENT` or `back-lerona` directory was present in the supplied workspace. The
protected sibling found here is `Cent.Module`; it is treated as SENT for safety.
No Cent-owned code, tests, settings, endpoints, schemas, migrations, preview data,
curriculum or deployment scripts were modified. No actual database migration or
deployment was run.

The sole edited file involved in shared host composition is
`Flashcards.Infrastructure/DependencyInjection.cs`. Its change adds a Lerona-only
sender branch and lazy OTP settings registration. Existing authentication defaults,
Cent's explicit `CentSession` scheme/policies, global rate limits, CORS, connections,
and provider defaults remain unchanged. Source inspection confirms Cent consumes
none of these phone-specific services. The existing Cent regression suite passed
**44/44** with zero skips. Git diff checks of all four `Cent.*` trees, host
`Program.cs`, `deploy/` and `liara.json` show no changes.

## Verification record

- Focused frontend authentication run: **119 passed, 3 suites** before the final
  additional throttle/response-validation test cases were added.
- Final auth-specific results in the complete run: **126 passed, zero failures**
  (32 screen, 56 cloud-client and 38 phone-validation tests).
- Final `tsc --noEmit`: passed.
- Final full ESLint: passed, zero errors; one existing unused `Sm2Scheduler` import
  warning in `src/features/study/domain/review-engine.test.ts`.
- Existing Cent suite: **44 passed, zero failures or skips**.
- Complete Lerona backend suite: **319 passed, zero failures, one pre-existing
  skipped live-flow case** (320 total). Includes OTP expiry/resend/attempt limits,
  equivalent-format identity, registration proof replay and concurrent verification,
  Kavenegar contract/error/configuration and runtime mock rejection tests. Backend
  compilation succeeded as part of this run; results: `TestResults/otp-maintenance.trx`.
- Production web export: passed using `expo export --platform web --max-workers 1`;
  output is ignored under `Frontend/.tools/otp-web-build`. No hosting/deployment occurred.
- Complete frontend run: **1231 passed, 14 failed**, 1245 total; **122 passing
  suites, two failing suites**. Results: `Frontend/.tools/otp-test-results.json`.
  That run loaded the navigation file before its fixture corrections. A subsequent
  broad navigation run passed 97/107, with ten failures (including timeouts).
- After the final fixture correction, a focused navigation run passed **6/6
  selected cases**, including authentication routing, direct sign-in entry,
  English/Persian correct-answer counters and stable study cancellation. Other
  navigation cases were deliberately not selected by that focused command.
- Final standalone navigation rerun: **102 passed, five failed**, 107 total.
  The previous timeout and study-order failures did not recur. Results:
  `Frontend/.tools/otp-navigation-final.json`. The remaining five names are listed
  below; this does not make the full frontend suite green.

An initial backend check overlapped edits and failed on an intermediate assembly;
a subsequent compile found a syntax error in a newly added test, corrected before
the final run. An initial full frontend run/export was interrupted to reduce
resource contention and rerun; these interrupted checks are not counted as passes.
Initial lint failed on the protected directory; a later lint caught and corrected
the new countdown's eager `Date.now()` initialization before final successful lint.

Live SMS delivery, staging SQL Server multi-instance concurrency, production
migration status and physical-device cold-start verification are unexecuted.
The existing `MobileCloudFlowTests` live-flow case is intentionally skipped by the
repository; a web export is not a physical-device test.

### Frontend failures requiring separate review

The complete run's failing suites were `shared/navigation/navigation.test.tsx`
(12 failures) and `features/study/presentation/study-card.test.tsx` (two failures).
They cover language/RTL assertions, reminder labels and scheduling arguments,
study ordering and timeouts. Existing `docs/visual-theme.md` already records
unresolved language/RTL, reminder and navigation failures from earlier work.

The two study-card assertions expect English `hello`/`greeting` content to be RTL
inside an RTL deck. Existing `deckContentStyle` instead determines writing
direction/alignment from actual content (with a center override), so it returns
LTR/left for that English text. The card-form preview assertion has the same
mismatch. Those production styling functions, components and assertions were
left unchanged under the explicit no-UI-change boundary. A separate contract
decision should determine whether the assertions or presentation behavior should
change; this task does not choose a new visual behavior.

Reminder navigation assertions also expect the older two-argument scheduling call
and permission-label controls that differ from current behavior. The actual
notification-service, reminder-plan and settings tests passed in the full run.
Do not treat the complete frontend suite as green or claim a clean baseline;
full pre-change baseline execution was not completed. Timeout-only failures
also require isolation from resource contention before treating them as product bugs.

Final unresolved navigation cases:

1. `app language switches visible navigation and RTL labels without changing deck language`
2. `card form validates required fields and previews RTL content with examples`
3. `daily reminder uses the notification service and reveals the local time control`
4. `permission denial keeps reminder off and offers device settings`
5. `unavailable notifications leave the reminder switch disabled`

The two unresolved study-card cases are `deck rtl / large controls only card
alignment and semantic typography` and `RTL alignment is preserved on the answer
face`. No production visual/notification behavior was changed to satisfy these
assertions. Across the complete run plus final navigation rerun, seven assertions
remain unresolved in two suites; a fresh single complete run after the fixture
changes was not executed.

### Reproducible commands

Run from `Backend`, using workspace-local `DOTNET_CLI_HOME=.dotnet` and
`NUGET_PACKAGES=.nuget/packages` as in the executed PowerShell commands:

```text
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --no-restore --verbosity minimal --logger "trx;LogFileName=otp-maintenance.trx" --results-directory TestResults
dotnet test backend/Cent.Tests/Cent.Tests.csproj --no-restore --verbosity minimal
```

Run from `Frontend` (installed dependencies were reused):

```text
node node_modules/typescript/bin/tsc --noEmit
node node_modules/eslint/bin/eslint.js .
node node_modules/jest/bin/jest.js --runInBand --watch=false --json --outputFile .tools/otp-test-results.json
node node_modules/jest/bin/jest.js --runInBand --runTestsByPath src/shared/navigation/navigation.test.tsx --silent --json --outputFile .tools/otp-navigation-final.json
node node_modules/expo/bin/cli export --platform web --max-workers 1 --output-dir .tools/otp-web-build
```

## Remaining work

1. Obtain Kavenegar key, verification entitlement, approved `%token` template name,
   restrictions/quotas and sender/fallback guidance. Confirm provider debug/simulation
   mode is disabled for real verification. Follow `KAVENEGAR_OTP_SETUP.md` and the
   backend `.env.example`; no live configuration has been installed.
2. Decide the transition policy for previously unverified phone/password accounts.
   Add explicit ownership verification/account-linking policies in a separate,
   Lerona-only change before relying on phone numbers for account recovery.
3. Review the existing PKCS 9.0.11 advisory warnings and EF Core 10.0.11/10.0.13
   assembly conflicts in a coordinated dependency change. Package/project files
   were left intact because the host graph includes Cent. Restore was repeated
   from available packages with `NuGetAudit=false`; this does not fix or clear the
   earlier advisories, and is not a successful vulnerability audit.
4. Check external tracing/proxy body and URL logging before activation; Kavenegar
   requires its key in the URL path. Confirm durable/shared edge limits and trusted
   proxy handling. Add challenge-row retention and global SMS budget monitoring
   through separately reviewed operational work.
