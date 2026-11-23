# Itatest authentication repair — implementation report

Date: 23 November 2026. Scope: Itatest frontend and Cent-owned backend only. No deployment, commit, push, production migration or live SMS send was performed.

## A. Root cause analysis

Confirmed by source inspection and regression tests:

1. Both Google implementations were reachable as alternative configured modes, rather than two simultaneously successful flows. Missing `Cent:Google:Mode` selected Authorization Code, and the frontend rendered a custom Google button for that flow. GIS was conditional rather than the sole Google path. The deployed mode was not inspected. The code exchange endpoints, client, PKCE helpers, callback route and browser attempt storage have been removed. Discovery now always describes GIS; obsolete mode settings cannot re-enable OAuth.
2. GIS completion accepted only `STUDENT` and `ADMIN` frontend roles, although Cent also supports SuperAdmin, UserManager, ContentManager, ExamManager and FinanceManager. The response validator now accepts the existing Cent role set.
3. Email/password remained available on the frontend and backend, contradicting the two-method requirement. These login/registration routes and UI were removed. Historical users, password hashes, Google subjects and account IDs remain intact.
4. Google failures still advised email login. Persian recovery messages now describe mobile login or explicit account recovery/linking.
5. SMS authentication did not exist in Cent. The implementation below is independent of Lerona's SMS services and settings.

The existing GIS verifier already checked RSA signature, issuer, audience, expiry, nonce, authorized party, issue time, verified email and subject. These checks were retained. No evidence established an incorrect live client ID, authorized origin, backend clock, production CSP/CORS or certificate connectivity. Earlier repository documentation mentions token-exchange connectivity problems; those historical reports were not independently reproduced. A real Google account round trip remains an external verification step.

## B. Final authentication architecture

**GIS:** Browser loads `https://accounts.google.com/gsi/client`, discovers the public client ID, obtains a five-minute Cent challenge, and renders Google's own button with its nonce. GIS returns an ID token through the JavaScript callback. Cent validates the signed token against Google's fixed certificate endpoint and atomically consumes the origin-bound challenge. Existing `GoogleSubject` is authoritative; email never silently merges accounts. New verified subjects create ordinary Cent users unless an existing email conflicts.

**SMS:** Cent normalizes Iranian mobile numbers to `+989xxxxxxxxx`, reserves durable phone/IP/global budgets in a serializable transaction, invalidates older challenges for that phone, generates a cryptographically random six-digit code, stores only its HMAC-SHA256 verifier, and sends one approved Melipayamak template request. Delivery failure invalidates the challenge while retaining the abuse budget. Verification reserves an attempt atomically before comparing the verifier, preventing parallel guesses from bypassing the five-attempt limit. Successful verification atomically consumes the challenge and creates/resolves its unique mobile identity and Cent session in one transaction.

**Accounts/sessions:** Both methods use `cent_users`, existing account IDs, `CentAuthEndpoints.StartSession`, `cent_sessions` and the `CentSession` scheme. Sessions remain random 256-bit opaque bearer tokens, hashed in the database, valid for seven days, restored through `/me`, revoked by logout and checked against current database roles. No refresh-token mechanism exists or was added. Frontend storage remains tab-scoped sessionStorage; it is accessible to same-origin JavaScript, so hosting CSP and XSS prevention remain necessary. Google tokens and OTPs are never persisted in browser storage or URLs.

Mobile-only accounts use a unique internal `mobile-<user-id>@accounts.itatest.invalid` placeholder to preserve the existing required email schema; this is not a verified email identity and is suppressed from session/profile responses. The phone mapping is a separate Cent-owned table with unique phone and user constraints. Google subjects retain their existing unique index and historical records. SMS uses a separate `CentSmsClock` registration and never replaces the shared host's `TimeProvider`.

**Linking:** The authenticated profile page offers mobile and GIS linking. Separate authenticated endpoints bind each challenge to the current canonical user ID. The new identity must be proven by OTP or a fully validated GIS token. Identities belonging to another account return 409; neither provider replaces a different identity or merges accounts automatically. Conditional assignment/unique constraints prevent concurrent Google linking from overwriting a subject. An established Cent session plus ownership proof is required; no unauthenticated email-based linking exists.

**Security:** OTP defaults: six digits, 120-second lifetime, 60-second resend cooldown, five attempts, single use. Default delivery budgets: five/phone/hour, twenty/IP/hour, one hundred/global/minute; durable database state applies across instances. Exhaustion locks sending until budget reset. Existing Cent auth endpoint IP limit and Cent admission limits remain active. Challenge IDs have 256 bits of randomness and are stored hashed. Verifiers include challenge, phone, linking target and code under an independent server key; comparisons use constant-time primitives. Provider HTTP logging is disabled because Melipayamak requires its API key in its path. Safe logs contain status/type only. No automatic delivery retry occurs, including ambiguous timeouts.

## C. Implementation and API contracts

All paths below start with `/api/cent/v1`. All responses use `Cache-Control: private, no-store`; errors follow `{ "message": "...", "code": "..." }`. No API response contains an OTP.

| Method/path | Input | Successful response |
| --- | --- | --- |
| GET `/auth/google/config` | none | `{enabled, mode:"GisIdToken", clientId}` |
| POST `/auth/google/gis/challenge` | `{}` | `{challengeId, nonce, browserBinding, expiresAt}` |
| POST `/auth/google/gis/complete` | `{challengeId, browserBinding, idToken}` | existing `{token, expiresAt, user}` |
| POST `/auth/sms/request` | `{phone}` | `{challengeId, phone, expiresAt, resendAt, serverTime}` |
| POST `/auth/sms/verify` | `{challengeId, code}` | existing `{token, expiresAt, user}` |
| POST `/auth/identities/sms/request` | `{phone}`, Cent bearer session | SMS challenge bound to user |
| POST `/auth/identities/sms/verify` | `{challengeId, code}`, same account session | existing session response for same user |
| POST `/auth/identities/google/gis/challenge` | `{}`, Cent bearer session | GIS challenge bound to user |
| POST `/auth/identities/google/gis/complete` | GIS completion, same account session | existing session response for same user |
| GET `/me`; POST `/auth/logout` | Cent bearer session | existing profile; 204 logout |

GIS POSTs additionally require an exact allowed `Origin`, `X-Cent-GIS: 1` and JSON. SMS JSON bodies are bounded to 2 KB; GIS completions to 16 KB. Unexpected/duplicate JSON fields are rejected. SMS resend uses `/sms/request`; no redundant resend endpoint exists. Statuses include 400 validation/malformed input, 401 invalid/expired/used OTP or token, 403 GIS origin rejection, 409 identity conflict, 413 oversized body, 415 unsupported content type, 429 limits with Retry-After and 503 delivery/configuration/transient failure. Unknown/expired/exhausted OTP failures share a generic message. The public request flow does not disclose whether an account exists.

Backend implementation: `CentSmsAuth.cs` (application service, normalization, endpoints), `CentSmsPersistence.cs` (entities/model), `CentMelipayamak.cs` (provider interface, adapter, Cent DI), and changes to `CentGoogleAuth.cs`, `CentGoogleGisAuth.cs`, `CentGoogleIdTokenValidator.cs`, `CentModule.cs`, `CentDbContext.cs`, `Entities.cs`, `Cent.Module.csproj`. `CentAdminConsole.cs` recognizes verified mobile identities in account metadata without changing role assignment rules. Tests use a new in-process `Cent.TestHost`, with no Lerona assemblies. Cent commerce tests now seed authenticated fixtures instead of calling the removed registration API. `Cent.Preview` has an explicit backed-up additive SQLite upgrade for existing local databases.

Frontend implementation: `LoginPage.tsx`, `GoogleSignIn.tsx`, `SmsSignIn.tsx`, `IdentitySettings.tsx`, `CommercePages.tsx`, `sms-auth.ts`, `auth-navigation.ts`, `google-identity-api.ts`, `google-identity.ts`, `cent.ts`, `App.tsx`, `main.tsx`, hosting headers, documentation and associated tests. Deleted: `google-auth.ts`, its obsolete tests, `GoogleCallbackPage.tsx`, the legacy OAuth browser suite and backend code-exchange tests. The historical Google attempt table/column names remain for GIS and compatibility; no historical identity data is deleted.

Migration: `20261123135254_AddCentSmsAuthentication` adds `cent_mobile_identities`, `cent_sms_challenges`, `cent_sms_budgets` and nullable `cent_google_attempts.LinkUserId`. It does not modify purchase, entitlement, progress, exam or Lerona tables.

## D. Verification results

Executed successfully:

- Full Cent backend suite: **129 passed, 0 failed, 0 skipped** (`TestResults/auth/cent-auth.trx`). This includes real RSA-signed synthetic GIS tokens, wrong signature/audience/issuer/expiry/nonce rejection, subject resolution, session/logout, replay/concurrency, OTP controls and existing commerce/curriculum regressions.
- After the final Cent-only clock isolation change: **32 SMS/provider tests passed, 0 failed**, including the additional host-clock/disabled-module isolation assertion (`TestResults/auth/cent-sms-final.trx`). These overlap the full suite; they are not 32 additional unique cases.
- Frontend full unit suite: **215 passed in 21 files**. GIS role handling, session and mobile contracts are covered. The subsequent final GIS transport run passed **43 tests**, including the added authenticated-linking header assertion; these overlap the full suite.
- Browser GIS suite: **14 scenarios passed**. Browser SMS suite: **7 scenarios passed**, including 320/390/1440px RTL, Persian digits, code focus, wrong code, resend/cooldown, refresh recovery, intended destination, restoration/logout and network/provider/rate failures. Evidence is under frontend `tests/browser/artifacts/` (ignored local artifacts).
- Frontend type check/production build and lint passed. Cent module and isolated-output Cent preview builds passed with zero warnings/errors. EF pending-model check passed. Idempotent SQL Server upgrade script generated and reviewed at `deploy/cent-sms-auth.sql`; it contains only Cent tables/history.
- Local preview smoke: GIS-only disabled discovery returned 200, disabled SMS returned 503 without delivery. The explicit SQLite upgrade was executed against a disposable simulated legacy schema missing the new tables/column; all three tables and the GIS column appeared, a backup was created, and the sentinel user ID and Google subject remained unchanged. No existing operator database was upgraded.

Initial frontend runs had two obsolete password/mode expectations and a leftover removed-variable reference; these were corrected. Initial backend runs were interrupted after stale dependency assets, Windows key-ring warmup/event-log failures, and old host/mode assertions. The final tests use an isolated Cent test host, local package caches, ephemeral test protection and production-equivalent rate rejection. A normal preview build encountered an existing process's file lock; the separate output build and disposable upgrade smoke passed without stopping that process. No shared package declaration or Lerona runtime behavior was changed to resolve those test-environment problems.

Live provider delivery, real Google consent, production ingress headers/CORS, Google Console settings and production SQL Server concurrency remain operator checks. Tests use synthetic Google signing keys and a mocked SMS sender; no live credentials are required. In-process SQLite tests do not establish production SQL Server lock behavior. Broader historical real-preview browser scripts were not executed; some still depend on retired password fixtures and need explicit disposable fixture sessions before reuse. This does not apply to the two updated auth browser suites. The complete shared/Lerona runtime suite was not run. An online package-advisory refresh was unavailable; offline restores used installed package caches.

## E. Exact external configuration

Google Cloud: use a Web application client. Authorized JavaScript origins: `https://itatest.ir`; local `http://localhost:5173` and `http://localhost` as required by Google's local setup guidance. Add `http://127.0.0.1:5185` or another exact loopback origin only if actually used. Origins have no path or trailing slash. Register alternate production hostnames only if served. Configure consent publishing/test users as appropriate. GIS JavaScript popup callbacks require no redirect URI or client secret. Remove obsolete Cent callback registrations after the rollback window; do not change any Lerona client configuration.

Backend variables (Cent only):

```dotenv
Cent__Enabled=true
ConnectionStrings__CentDb=<explicit Cent database connection from secret store>
Cent__AllowedOrigins__0=https://itatest.ir
Cent__Google__Enabled=true
Cent__Google__ClientId=<public Web client ID>
Cent__Sms__Enabled=true
Cent__Sms__VerifierKey=<base64 of at least 32 cryptographically random bytes>
Cent__Sms__LifetimeSeconds=120
Cent__Sms__ResendSeconds=60
Cent__Sms__MaxAttempts=5
Cent__Sms__PhonePerHour=5
Cent__Sms__IpPerHour=20
Cent__Sms__GlobalPerMinute=100
Cent__Sms__Melipayamak__ApiKey=<independently provisioned provider key>
Cent__Sms__Melipayamak__BodyId=<approved shared-template numeric ID>
Cent__Sms__Melipayamak__Endpoint=https://console.melipayamak.com/api/send/shared/
Cent__Sms__Melipayamak__TimeoutSeconds=10
```

Use the same independent verifier key on all Cent replicas. Rotation invalidates outstanding OTP verifiers and changes rate-budget hash keys; coordinate rotation and avoid using it to reset abuse limits. Configuration clamps prohibit weaker settings than five attempts, two-minute maximum lifetime or sixty-second minimum cooldown. OTP length remains fixed at six digits. Do not store these secrets in Git or Vite variables. `Cent__Google__Mode`, `Cent__Google__ClientSecret` and `Cent__Google__RedirectUri` are obsolete and unused.

Melipayamak: provision an approved shared service template with exactly one argument (OTP code), an active API key, sufficient credit and delivery authorization for Iranian mobile recipients. The verified existing integration contract is POST `https://console.melipayamak.com/api/send/shared/{apiKey}` with `{bodyId,to:"09...",args:[code]}`, accepting only a positive `recId` (number or numeric string). This API selects its sender through the shared template; no separate sender, username/password or Kavenegar parameters apply. Endpoint configuration is restricted to this exact HTTPS URL to prevent secret exfiltration. Retry policy is deliberately zero retries. Authentication, credit, recipient/template errors, malformed responses, network errors and timeouts all fail safely with 503. Neither API keys nor raw provider responses are logged. Do not attach HTTP body/URL tracing or external telemetry to this client.

Production frontend: `VITE_BASE_PATH=/`, `VITE_CENT_API_URL=https://lerona-app.com/api/cent/v1` for the currently documented separate API host, `CENT_HOSTING=cloudflare` when using the existing Cloudflare build. Confirm the actual operator API hostname before release; its Cent route shares hosting only, never Lerona identities. Local: `VITE_BASE_PATH=/cent-s/`, `VITE_CENT_API_URL=/api/cent/v1`, `CENT_BACKEND_URL=http://127.0.0.1:5108`, backend Development environment, and exact Cent allowed frontend origin. The public client ID is discovered from the backend, not duplicated in frontend variables.

Hosting: HTTPS at ingress; native SPA fallback; no caching/analytics of auth bodies; no secrets in bundles. Allow only required GIS additions in the existing CSP: script `https://accounts.google.com/gsi/client`, frame/connect `https://accounts.google.com/gsi/`, style `https://accounts.google.com/gsi/style`, plus the actual API in connect-src. Retain other directives. Login and profile linking need `Cross-Origin-Opener-Policy: same-origin-allow-popups` for popup fallback; `_headers` contains these scoped settings. GIS views temporarily use Google's recommended referrer policy. Keep backend clocks synchronized. Configure trusted proxy IP handling at the existing hosting boundary so `RemoteIpAddress` represents the client; do not trust arbitrary forwarded headers. No shared middleware was changed for this purpose.

References: [Google setup, origins, CSP and referrer policy](https://developers.google.com/identity/gsi/web/guides/get-google-api-clientid), [GIS callback/button API](https://developers.google.com/identity/gsi/web/reference/js-reference). Melipayamak request conventions were verified against the existing installed adapter read-only; no credentials were copied.

## F. Controlled deployment preparation

1. Inventory and back up the Cent database and identify password-only accounts requiring recovery. Active sessions can use profile linking before expiry. For signed-out password-only users, use an operator-supervised ownership recovery process against the original user ID. Never auto-merge by Google email or an unverified phone. Do not release without an account-recovery decision for affected users.
2. Provision independent Cent secrets/template and Google origins. Verify production connectivity to Google's fixed certificate endpoint and Melipayamak with normal TLS. Complete a controlled real-phone/real-Google acceptance test before general release.
3. Generate/review a Cent-only migration script against the recorded previous migration:

```powershell
dotnet ef migrations script 20261122102343_SubjectCommerce 20261123135254_AddCentSmsAuthentication --project backend/Cent.Module --startup-project backend/Cent.Module --context CentDbContext --idempotent --output deploy/cent-sms-auth.sql
```

Apply the reviewed script using the explicitly selected Cent connection/database and `cent_migrations_history`. First inspect the actual Cent migration history: this incremental script assumes `20261122102343_SubjectCommerce` is already applied. If the target is older, review/generate its preceding Cent upgrades in order rather than skipping them. Never invoke the Lerona DbContext or its migration history. The migration and SQL were generated locally; applying them to an existing database is an operator action. Fresh disposable previews use EnsureCreated. Existing SQLite previews can use the explicit `--upgrade-sms-auth true` Cent preview flag, which backs up the database before additive changes; run the historical `--upgrade-google-auth true` first only if that schema is missing. Do not reset preview data to fix schema errors.

4. Deploy schema first, then Cent backend, then frontend in a controlled maintenance window. Old frontends still invoking password/OAuth routes will receive 404, so avoid prolonged mixed-version traffic. Existing Cent sessions and Google subject associations remain compatible. Verify `/me`, logout, protected destinations, both methods, linking conflicts and existing purchases/progress. Do not mutate monetization rules to test authentication.
5. Rollback application artifacts together while leaving the additive schema intact. Do not run migration Down after mobile identities are used: it would discard identity mappings. Old artifacts cannot authenticate new SMS-only users; disable new SMS requests and retain the corrected backend or schedule recovery before rollback. Keep database backups and an explicit rollback owner.

## G. Lerona isolation

No Lerona frontend, authentication source, entities, migrations, database structures, SMS templates, settings or environment files were modified. No shared startup or composition file was changed. Cent registrations remain inside the existing `Cent:Enabled` guard. No Cent runtime reference to a Lerona auth assembly or service was introduced. The provider adapter uses only `Cent:Sms:*`. Cent test fixtures use synthetic environment values in their own process; no deployed environment was changed. Shared hosting and the historical database connection fallback already existed; deployment instructions require an explicit Cent connection. Lerona runtime tests were not executed successfully and are not claimed as passed.

## H. Remaining external gates

- Operator Google Console/client/origin validation and one real Google sign-in on `https://itatest.ir`.
- Valid independently configured Melipayamak key/template/credit and one controlled real-phone delivery test.
- Review/apply the Cent migration and verify SQL Server concurrency at staging.
- Decide recovery for existing password-only accounts before rollout.
- Verify shared production-host composition, reverse-proxy client IPs, TLS, CORS, CSP and scoped headers at staging. No deployment has been attempted.

## Changed-file inventory

Git status below includes additions/deletions and excludes ignored build/test artifacts. The Backend and Itatest repositories were clean at initial inspection. Lerona frontend status is clean.

Backend repository:

```text
 M backend/Cent.Module/Cent.Module.csproj
 M backend/Cent.Module/CentAdminConsole.cs
 M backend/Cent.Module/CentDbContext.cs
 M backend/Cent.Module/CentGoogleAuth.cs
 M backend/Cent.Module/CentGoogleGisAuth.cs
 M backend/Cent.Module/CentGoogleIdTokenValidator.cs
 M backend/Cent.Module/CentModule.cs
 M backend/Cent.Module/Entities.cs
 M backend/Cent.Module/Migrations/CentDbContextModelSnapshot.cs
 M backend/Cent.Preview/Program.cs
 M backend/Cent.Preview/README.md
 M backend/Cent.Tests/Cent.Tests.csproj
 M backend/Cent.Tests/CentApiTests.cs
 M backend/Cent.Tests/CentCommerceTests.cs
 D backend/Cent.Tests/CentGoogleAuthTests.cs
 M backend/Cent.Tests/CentGoogleGisTests.cs
 M docs/CENT-GOOGLE-AUTHENTICATION.md
?? backend/Cent.Module/CentMelipayamak.cs
?? backend/Cent.Module/CentSmsAuth.cs
?? backend/Cent.Module/CentSmsPersistence.cs
?? backend/Cent.Module/Migrations/20261123135254_AddCentSmsAuthentication.Designer.cs
?? backend/Cent.Module/Migrations/20261123135254_AddCentSmsAuthentication.cs
?? backend/Cent.Preview/SmsAuthUpgrade.cs
?? backend/Cent.TestHost/Cent.TestHost.csproj
?? backend/Cent.TestHost/CentTestEntry.cs
?? backend/Cent.Tests/CentMelipayamakTests.cs
?? backend/Cent.Tests/CentSmsAuthTests.cs
?? backend/Cent.Tests/TestAccounts.cs
?? backend/Cent.Tests/TestIsolation.cs
?? deploy/cent-sms-auth.sql
?? docs/CENT-AUTHENTICATION.md
```

Itatest frontend repository:

```text
 M .env.example
 M GOOGLE-GIS.md
 M README.md
 M public/_headers
 M src/App.tsx
 M src/components/GoogleSignIn.tsx
 M src/main.tsx
 M src/pages/CommercePages.tsx
 D src/pages/public/GoogleCallbackPage.tsx
 M src/pages/public/LoginPage.tsx
 M src/services/cent.ts
 D src/services/google-auth.test.ts
 D src/services/google-auth.ts
 M src/services/google-identity-api.test.ts
 M src/services/google-identity-api.ts
 M src/services/google-identity.test.ts
 M src/services/google-identity.ts
 M src/services/session.test.ts
 M tests/browser/README.md
 M tests/browser/check-appearance-toggle.mjs
 D tests/browser/check-google-auth.mjs
 M tests/browser/check-google-gis.mjs
 M tests/browser/check-public.mjs
 M tests/browser/package.json
 M tests/browser/run-themed-auth.mjs
?? AUTHENTICATION.md
?? src/components/IdentitySettings.tsx
?? src/components/SmsSignIn.tsx
?? src/services/auth-navigation.ts
?? src/services/sms-auth.test.ts
?? src/services/sms-auth.ts
?? tests/browser/check-auth.mjs
```

