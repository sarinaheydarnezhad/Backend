# Cent Google authentication

This change applies only to `Cent.Module`, its tests and local preview. Lerona's `/api/v1/auth/google`, JWTs, users, migrations and authentication configuration are separate and unchanged. Cent continues to use its existing opaque bearer sessions: seven-day expiry, SHA-256 hashes at rest, immediate revocation through logout, and the same `{token, expiresAt, user}` response as password login. Existing passwords and administrator bootstrap still work.

## Flow and account policy

The separate frontend uses the OAuth authorization-code flow with S256 PKCE. Google redirects to a **fixed frontend callback**, which sends the code to the backend. The backend alone exchanges it with Google's token endpoint, using its client secret. It validates the ID token with Google's official `Google.Apis.Auth` library (RS256 signature, Google issuer, client audience, expiry and issued-at), then checks nonce, optional authorized party (`azp`), optional not-before, verified email and subject. No Google access token, refresh token or client secret is returned to the frontend or stored in the database. Only `openid email profile` is requested; no offline access is requested.

Each login attempt lasts five minutes. The database stores SHA-256 hashes of state, nonce and the high-entropy PKCE verifier. The browser holds the raw verifier in tab-scoped `sessionStorage`. An atomic conditional delete consumes the attempt before any token exchange, including cancellation and failure; a replay or concurrent second callback cannot exchange it again. Failed exchanges require a fresh start. This database state works across server replicas without sticky sessions or data-protection keys. Expired attempts are deleted when a new attempt starts.

The unique, case-sensitive Google `sub` identifies the account. A new Google user is always a STUDENT with no password. If the verified email already belongs to **any** Cent user without that exact Google subject, the backend returns `409 google_account_conflict`. It never links by email and exposes no linking endpoint. The user must use the existing method; explicit, reauthenticated linking would be separate work. Returning Google users keep their Cent profile/email and role even if their Google email changes. Database unique indexes enforce subject/email uniqueness during concurrent registrations.

## Configuration (environment variables)

Keep secrets in the server/hosting secret environment. Do not put the client secret in appsettings, a frontend build variable, source control, URLs or logs.

| Variable | Value |
| --- | --- |
| `Cent__Enabled` | `true` (existing Cent switch) |
| `Cent__Google__Enabled` | `true`; absent/false disables the new flow |
| `Cent__Google__ClientId` | Google **Web application** OAuth client ID |
| `Cent__Google__ClientSecret` | Secret for that same client, backend only |
| `Cent__Google__RedirectUri` | Exact frontend callback URL registered with Google |
| `Cent__AllowedOrigins__0` | Exact frontend origin, e.g. local `http://localhost:5173`; no path, wildcard or trailing slash |
| `Cent__AllowedOrigins__1` | Another explicit origin only if needed |
| `ConnectionStrings__CentDb` | Existing optional Cent database connection; otherwise existing FlashcardsDb fallback applies |

Configuration uses the hosts' standard .NET environment-variable provider (`__` maps to `:`). No Google credentials or production URLs are supplied in this repository. Google sign-in returns 503 when disabled, missing credentials, or configured with an unsafe callback. The callback must belong to an explicit Cent allowed origin, contain no query, fragment or userinfo, and use HTTPS. HTTP callback URLs are permitted only for loopback hosts in Development. CORS permits exact origins and does not enable credentialed cookies; authentication uses bearer tokens. Wildcards, origins with paths/userinfo, and non-loopback HTTP origins are rejected. Existing Lerona CORS settings are untouched.

## Google Cloud OAuth configuration

1. Use a Google Cloud project owned by the deployment operator. In **Google Auth Platform**, configure Branding (app name, support/contact email, homepage/privacy/terms URLs when required) with the actual Cent deployment details. Verify actual production domains as Google requires.
2. Configure Audience: External for public Google users, or Internal only if intentionally restricting to your Workspace organization. In Testing status, add the intended Google test users. Publish when ready for production and complete any review requested by Google.
3. Under Data Access request only `openid`, `email` and `profile`.
4. Under Clients create an OAuth client of type **Web application**. Prefer separate local and production clients. Supply the generated client ID and secret to the backend environment.
5. Local Authorized JavaScript origin: `http://localhost:5173`. Local Authorized redirect URI: **`http://localhost:5173/cent-s/auth/google/callback`**. Set `Cent__Google__RedirectUri` to exactly that URI. The frontend must serve its callback route at that path, including on a direct navigation/reload. `127.0.0.1` and `localhost` are different origins; register/configure another exact URI if you use a different frontend address.
6. Production Authorized JavaScript origin: **the actual HTTPS Cent frontend origin (not yet supplied)**. Production Authorized redirect URI: **`<actual-HTTPS-frontend-origin>/cent-s/auth/google/callback`** if the deployed frontend retains `/cent-s`; otherwise use its actual callback path. Set `Cent__Google__RedirectUri` to the identical absolute URL and `Cent__AllowedOrigins__0` to its origin. Do not register the backend's `/google/complete` endpoint as a Google redirect URI.

The production frontend origin/base path, backend base URL, client ID/secret, Google project/consent-screen details, and intended database connection are deployment inputs still required. The known local backend base URL is `http://127.0.0.1:5108`. No backend GET callback is needed: the browser callback is on the frontend and the backend exchange endpoint is POST-only.

References: [Google OpenID Connect flow](https://developers.google.com/identity/openid-connect/openid-connect), [Google token endpoint and PKCE errors](https://developers.google.com/identity/openid-connect/reference), [official .NET validator](https://docs.cloud.google.com/dotnet/docs/reference/Google.Apis/latest/Google.Apis.Auth.GoogleJsonWebSignature).

## Database deployment

Migration: **`AddCentGoogleAuthentication`** in `Cent.Module/Migrations`. It adds nullable `GoogleSubject` to `cent_users`, a filtered unique binary-collation index, and `cent_google_attempts` with an expiry index. Existing user/password/session data is preserved. No Lerona tables/history are altered; no migrations run at shared-host startup.

Back up the intended database and review `backend/Cent.Module/Deployment/cent-google-auth.sql` before applying it. It is an idempotent **delta** requiring the existing InitialCent and Curriculum migrations. Apply the migration before running the updated binary with Cent enabled, even if Google sign-in remains disabled, because the user model now includes the new column. You can alternatively apply through EF with the explicit intended connection environment:

```powershell
dotnet ef database update --context CentDbContext --project backend/Cent.Module --startup-project backend/Cent.Module
```

The design-time factory defaults to local CentDev if neither connection environment variable is set. Always identify the intended target explicitly. Do not run Down/drop columns after users register with Google; disabling `Cent__Google__Enabled` stops Google sign-in while retaining accounts and existing sessions.

For a **new** local SQLite preview, normal startup creates the current schema. For an **existing** preview database, stop that preview process and run once:

```powershell
dotnet run --project backend/Cent.Preview/Cent.Preview.csproj -- --upgrade-google-auth true
```

This explicit command takes a timestamped `.before-google-auth-*.bak` SQLite backup, adds the nullable column and indexes, and creates the attempt table without replacing existing data. It is repeatable. It does not modify any database unless you run it. The command starts the preview after upgrading; stop it normally afterward if needed.

## Frontend API contract

All paths below are relative to the configured backend base URL. Responses carry `Cache-Control: private, no-store`. The frontend must use HTTPS in production, `credentials: "omit"`, and the existing bearer-token handling after login. No Google SDK is required in the frontend.

New endpoints are `POST /api/cent/v1/auth/google/start` and `POST /api/cent/v1/auth/google/complete`. Existing `POST /api/cent/v1/auth/login` retains its request/response contract and now explicitly rejects Google-only accounts with no password. Registration, bootstrap, `/me` and logout retain their contracts.

### 1. Start

`POST /api/cent/v1/auth/google/start` with no body, or `{}`. Returns `200`:

```json
{
  "authorizationUrl": "https://accounts.google.com/o/oauth2/v2/auth?...",
  "state": "<43-character-random-state>",
  "codeVerifier": "<43-character-random-PKCE-verifier>",
  "expiresAt": "<UTC timestamp>"
}
```

Store `{state, codeVerifier, expiresAt}` in tab-scoped sessionStorage **before** setting `window.location` to `authorizationUrl`. Never put the verifier or a Cent session token in a URL, log or analytics event. Do not generate a second authorization URL or edit its parameters. Starting another login in the same tab should replace the locally stored attempt.

### 2. Handle the frontend callback

At the exact configured frontend callback route, read `state` and either `code` or `error` from the Google query string. Require exactly one state and exactly one code/error (reject ambiguous duplicate parameters). Read the saved attempt from sessionStorage; require the returned state to equal the saved state and the attempt not to have expired. Reject callbacks without a locally initiated attempt; never accept a verifier or a replacement saved state from URL parameters. The saved verifier binds the flow to that browser tab independently of cookies.

Copy the needed callback values into memory, immediately remove the query string using `history.replaceState`, and remove the saved attempt. Use `Referrer-Policy: no-referrer` and `Cache-Control: no-store` on the frontend callback page; render it without third-party scripts/analytics or resources that could collect its query string. Then call:

`POST /api/cent/v1/auth/google/complete`, `Content-Type: application/json`:

```json
{
  "state": "<returned-state-matching-the-saved-state>",
  "codeVerifier": "<saved-PKCE-verifier>",
  "code": "<Google-authorization-code>"
}
```

For cancellation send `{ "state": "...", "codeVerifier": "...", "error": "access_denied" }` instead. Never send an ID token, Google access token, role, user ID, client secret or browser-selected redirect URI. Completion always uses the server's fixed client/redirect settings.

Successful completion returns the **existing Cent login response**, `200`:

```json
{
  "token": "<64-character-opaque-Cent-session-token>",
  "expiresAt": "<UTC timestamp, seven days after issue>",
  "user": { "id": "<UUID>", "name": "...", "email": "...", "role": "STUDENT" }
}
```

Returning users retain their stored role. Use `Authorization: Bearer <token>` on Cent endpoints. `GET /api/cent/v1/me` and `POST /api/cent/v1/auth/logout` behave as before. Never use the Google ID token as an application bearer token.

### Errors

Authentication errors use Cent's `{ "message": "<safe text>", "code": "..." }` shape. No Google token response, credential, or exception details are echoed.

| Status/code | Frontend action |
| --- | --- |
| `400 google_cancelled` | Show cancellation; offer a fresh sign-in |
| `400 google_authorization_failed` | Show failure; offer a fresh sign-in |
| `401 invalid_google_credentials` | State/proof missing or invalid, expired/replayed attempt, missing/expired code, invalid token/claims; restart |
| `409 google_account_conflict` | Ask the user to use the existing Cent sign-in method; do not auto-link |
| `503 google_unavailable` | Disabled/missing/unsafe configuration or unavailable Google token/key service; retry with a new attempt later |
| `503 temporarily_unavailable` | Existing Cent database/network error handler; start a new attempt when available |
| `429` | Existing Cent admission/auth rate limit; observe Retry-After when present |

Malformed JSON/missing nonoptional request fields may use the host's standard 400 response. Both new routes share Cent's existing 10 auth requests/minute/IP limit and four concurrent-request admission budget. Each normal sign-in uses two auth requests. A browser reload or duplicate callback must not retry completion indefinitely; all failures after attempt consumption require a new start.

## Verification

```powershell
dotnet restore backend/Cent.Tests/Cent.Tests.csproj
dotnet test backend/Cent.Tests/Cent.Tests.csproj --no-restore
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj
dotnet build backend/Flashcards.slnx
```

The automated suite uses isolated SQLite and a fake Google exchange for deterministic endpoint/account/session tests, plus Google's real library for malformed/unsigned token rejection and explicit nonce/authorized-party/time checks. No test credentials can sign in to real Google. SQL Server migration/model checks do not prove live SQL Server locking or a real Google round trip.

Implementation verification: the full solution build passed with zero errors, all 44 Cent tests passed, and the Lerona regression suite passed 271 tests with its one pre-existing live-flow test skipped. An isolated temporary preview database was upgraded twice: both backups were created, existing user/password data was preserved, and the new attempt table was usable. The build still reports existing EF assembly-version and standalone Lerona infrastructure dependency warnings. Cent pins the patched PKCS dependency for its Google library; unrelated project files were not changed to resolve their separate warnings.

For a real local test:

1. Create the local Web application OAuth client and consent-screen test user as above. Configure the environment variables, `ASPNETCORE_ENVIRONMENT=Development`, and `Cent__AllowedOrigins__0=http://localhost:5173`. Start the preview (upgrading its existing database first if necessary).
2. Implement the frontend callback contract at `http://localhost:5173/cent-s/auth/google/callback`, using `http://127.0.0.1:5108` as backend base URL or the frontend's existing proxy.
3. Start, choose a test Google account, and complete. Check that one STUDENT exists, `/me` succeeds with the returned Cent bearer token, and `/api/v1/me` and Cent admin APIs reject that token. Log out and confirm `/me` rejects the revoked session. Repeat Google login and confirm the same Cent user ID.
4. Cancel at Google; check no session is issued. Replay completion, alter state/verifier, wait over five minutes, or reuse/expire the code: check rejection and restart behavior. Remove or misconfigure credentials and verify a safe failure.
5. Register a password user using a Google test account's email, then attempt Google sign-in. Check 409 and that password login still works with no Google association added.
6. Check preflight from the allowed frontend origin and denial of CORS headers for an untrusted origin. Never configure `*` as an origin.
7. On staging SQL Server, review/apply the migration and exercise simultaneous registrations/callbacks before deployment. Confirm unique subject/email constraints, one-time consumption, migration history isolation and backup/restore. Real Google, staging SQL Server and production deployment require the missing operator inputs and are not performed by this code change.

## Files added or modified

| Area | Files |
| --- | --- |
| Authentication and dependency registration | `backend/Cent.Module/CentGoogleAuth.cs`, `backend/Cent.Module/CentModule.cs`, `backend/Cent.Module/Cent.Module.csproj` |
| Cent database model | `backend/Cent.Module/Entities.cs`, `backend/Cent.Module/CentDbContext.cs` |
| Migration | `backend/Cent.Module/Migrations/20261109184203_AddCentGoogleAuthentication.cs`, `backend/Cent.Module/Migrations/20261109184203_AddCentGoogleAuthentication.Designer.cs`, `backend/Cent.Module/Migrations/CentDbContextModelSnapshot.cs` |
| Deployment | `backend/Cent.Module/Deployment/cent-google-auth.sql`, `backend/Cent.Module/Deployment/README.md` |
| Local preview | `backend/Cent.Preview/GoogleAuthUpgrade.cs`, `backend/Cent.Preview/Program.cs`, `backend/Cent.Preview/README.md` |
| Tests | `backend/Cent.Tests/CentGoogleAuthTests.cs`, `backend/Cent.Tests/CentApiTests.cs` |
| Documentation | `docs/CENT-GOOGLE-AUTHENTICATION.md`, `CENT.md` |
