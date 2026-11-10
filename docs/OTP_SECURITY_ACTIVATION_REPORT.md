> Historical document. Superseded by [the SMS OTP-only policy](SMS_OTP_ONLY_REPORT.md). Lerona password/Google/activation routes now return 410; Cent is unchanged.

# Lerona mandatory SMS verification and Kavenegar activation report

## Activation status

**Implemented in the repository; not deployed or live-SMS verified.** The ignored
file `D:\Lerona\Backend\.env.kavenegar.local` was created with the requested key
and template placeholders. No real API key was supplied to the agent, printed,
logged, committed or placed in frontend configuration. No real SMS was sent.

`git check-ignore .env.kavenegar.local` confirms the ignore rule; `git ls-files`
does not list the file. `Start-LeronaLocal.ps1 -CheckConfiguration` loaded the
settings through .NET's environment provider and reported:

```json
{"environmentLoaded":true,"kavenegarSelected":true,"credentialsAndTemplateConfigured":false,"providerChecked":false,"providerDebugModeDisabled":false,"smsSent":false,"realDeliveryVerified":false}
```

This is an expected nonzero, fail-closed result for placeholders. The script parses
dotenv as data and passes values through process environment, not command-line
arguments. The independent `Lerona.SmsCheck` tool outputs booleans only and does
not start the API or touch a database. `-CheckProvider` is read-only and sends no SMS.

## Policy and endpoints

No SMS verification means no new active account, session, refresh or protected API
access. The backend never uses request-supplied verification flags as evidence.

| Endpoint under `/api/v1` | Change |
| --- | --- |
| `POST /auth/phone/register-password` | Disabled with `403 phone_verification_required`; service/frontend compatibility methods also fail closed |
| `POST /auth/register` | Requires phone-bound SMS registration proof; bare email signup is blocked |
| `POST /auth/google` | No new user creation; existing users require durable SMS verification |
| `POST /auth/phone/activation/request` | New credential-confirmed legacy activation request; password + existing phone/email, or existing Google subject + proposed mobile; no session issued |
| `POST /auth/phone/otp/request` | Public purposes remain register/login; clients cannot request arbitrary user-bound activation |
| `POST /auth/phone/otp/verify` | Activation consumes a valid account-bound OTP, records verification, revokes old sessions and creates the new session transactionally |
| `POST /auth/phone/register` | Creates a verified user/session only after transactional consumption of valid signup proof |
| Password login, Google login, refresh and all protected Lerona routes | Durable verification and active-session checks apply independently of frontend state |

The existing cryptographic OTP generation, keyed hashes, expiry, resend/hourly
quotas, attempt limits, purpose binding and replay prevention remain in use.
Registration proof is single-use and phone-bound. User-row locks serialize session
creation/refresh with activation, and OTP-row writes serialize code/proof consumption.

Legacy records retain their IDs, content, profiles and password credentials. They
are not grandfathered into verification. Activation proves the existing account
credential/Google subject and then SMS possession. An occupied number is never
merged, and an existing phone cannot be silently replaced. Old sessions remain
revoked after activation; archived accounts cannot activate.

New JWTs include a signed `phone_verified` claim. New token responses include
`phoneVerified:true`; cached legacy frontend credentials lacking this marker are
cleared without deleting the owner's local learning data. This compatibility
marker is not trusted by the backend; current database verification is authoritative.

## Files changed

Core backend ownership remains Lerona-only:

- `Flashcards.Domain/Entities.cs`: verification fields and activation binding.
- `Flashcards.Infrastructure/Auth/{PhoneAuthService,PhoneVerification,TokenService,AuthService,UserService,GoogleAuthService}.cs`:
  mandatory verification, credential-bound activation, session enforcement and disabled bypasses.
- `Flashcards.Api/Endpoints/{PhoneAuthEndpoints,V1Endpoints}.cs`: activation contract,
  verified email signup and safe authentication error mapping.
- `Flashcards.Infrastructure/Auth/KavenegarOtpSender.cs`: reject placeholders,
  read-only provider debug-mode check, safe transport/cancellation errors.
- `Flashcards.Infrastructure/DependencyInjection.cs`: Lerona JWT claim validation;
  earlier sender-specific registration remains scoped to Lerona.
- `Flashcards.Infrastructure/Persistence/FlashcardsDbContext.cs`, its model snapshot,
  and `20261110145342_RequireVerifiedPhoneAuthentication` migration/designer.
- `deploy/20261110145342_RequireVerifiedPhoneAuthentication.sql`: reviewed idempotent
  SQL for this migration only, generated from the previous `AddSm2ReviewSystem` migration.
- `scripts/Start-LeronaLocal.ps1`, `backend/Lerona.SmsCheck/*`, ignored local dotenv,
  configuration examples and documentation.

Frontend changes are confined to authentication screen/state, cloud/sync adapters,
token validation, localized authentication messages, tests and documentation.
Existing Input/Button/Text styling, theme, layout primitives and app navigation
behavior are preserved. Signup first verifies SMS, then collects account details.
Activation supports legacy password/email and Google identities without an earlier
authenticated session. Existing session binding, SecureStore, guest-data/name gates
and navigation are reused.

Test fixtures now provision verified accounts through the real OTP service/API
using capture senders found only in the test assembly. No runtime test flag or
mock provider can enable production authentication. Existing account/session,
sync, backup, deck and subscription fixtures were updated to the new policy.

## Database and Cent safety

The migration adds only nullable `Users.PhoneVerifiedAtUtc`,
`Users.VerifiedPhoneNumber` and `OtpChallenges.ActivationUserId`, then revokes
legacy Lerona sessions with no verification evidence. It does not guess phone
ownership, delete users/content or touch Cent objects. Its generated SQL was
reviewed and was **not applied** to any real database.

No files in `Cent.Module`, `Cent.Preview`, `Cent.Curriculum` or `Cent.Tests` were
modified. No Cent model, migration, table, setting or API contract changed. The
shared API `Program.cs`, CORS, rate-limit policies, connections and package versions
remain unchanged. Lerona's JWT validation is distinct from Cent's explicit
`CentSession` scheme. The existing Cent suite passed 44/44 tests with the new
Lerona verification/session policy loaded in the shared host.

The migration unit test generates SQL without opening its placeholder SQL Server
connection, verifies no Cent/older-migration/drop statements, and checks snapshot
consistency. All integration database writes use isolated test SQLite storage.
No deployment, production migration or destructive database operation occurred.

## Frontend failure repairs

The previously unresolved assertions were repaired without production UI changes:

- Language tests check the actual localized text direction and supported English/
  Persian choices, rather than requiring a now-absent global RTL wrapper/Arabic button.
- English card content remains LTR within an RTL deck; a separate Persian content
  case verifies actual RTL question/answer styling. Mixed preview text is tested
  by content direction.
- Reminder tests mock native permission inputs, because composition copies service
  methods; spying on the original service object did not change the application's
  captured permission method. Scheduling assertions include the current plan argument.
- Navigation fixtures use deterministic study order and sortable synthetic IDs,
  with real existing logout/guest-data routes in the test route map.
- Full native-stack integration tests have file-local bounded 30-second case and
  5-second query budgets. Unit test budgets and production timers are unchanged.

## Verification record

- Full Lerona backend run: **315 passed, zero failed, one existing skipped live-flow
  case** (316 total), recorded in `TestResults/otp-security-final.trx`.
- Cent isolation run: **44 passed, zero failures/skips**, recorded in
  `TestResults/cent-otp-security-isolation.trx`.
- Focused frontend authentication/client run: **81 passed, zero failures**.
- Final navigation run: **107 passed, zero failures**.
- Updated study-card suite: passed, including Persian/LTR direction coverage.
- TypeScript: passed. Full ESLint: passed with one pre-existing unused
  `Sm2Scheduler` import warning in `review-engine.test.ts`.
- Production web export: passed, 3473 modules; ignored output under
  `Frontend/.tools/otp-security-web-build`.
- Final sender-only transport/environment run: **33 passed, zero failures**,
  including environment-to-backend-adapter binding, placeholder rejection, provider
  debug rejection, timeouts, malformed responses and sanitized caller cancellation.
  Results: `TestResults/kavenegar-final.trx`.
- Fresh complete frontend/backend runs on the final compiled/source state are
  recorded here after their in-progress commands complete.

Intermediate failures were resolved: old tests expecting unverified registration,
an obsolete migration count, fixture OTP setup exhausting an unrelated auth quota,
duplicate text queries and resource-contention navigation timeouts. The first full
frontend run during concurrent builds failed on navigation timeouts; the corrected
navigation file subsequently passed all 107 cases. No test failure is silently
converted into a successful delivery or authentication claim.

## Remaining manual input/actions

1. Enter the real key locally and supply the exact approved template name. Do not
   send the key through chat. Confirm Verify/Lookup entitlement, Account/Config
   read permission, backend IP allowlist, balance/quotas and provider debug mode off.
2. Explicitly approve a test phone number/SMS charge and identify an isolated local
   or staging API/database. No production operation is authorized by this report.
3. Review/apply the Lerona-only migration through the approved staging process;
   it intentionally revokes legacy Lerona sessions. The code never auto-applies it.
4. Run the safe checker, then test an actual handset receipt and backend OTP
   verification. Provider acceptance alone is insufficient. A registration-purpose
   verification can prove delivery without creating an account; discard its proof.
5. Perform staging SQL Server multi-replica and physical-device cold-start/refresh
   tests. Existing legacy accounts with lost credentials or non-Iranian stored
   numbers need a separately reviewed recovery process; no bypass is provided.
6. Production deployment requires separate approval. Existing EF version conflicts
   and PKCS advisory history remain a coordinated dependency-review item. NuGet
   audit retrieval failed in the restricted network; no clean vulnerability audit
   is claimed and package versions were not changed under the Cent boundary.

The exact local loader, endpoints, settings and support checklist are documented
in [KAVENEGAR_OTP_SETUP.md](KAVENEGAR_OTP_SETUP.md).
