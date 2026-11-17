# ItaTest Google Identity Services: backend and frontend contract

This document describes the implemented Cent-only GIS API. The existing authorization-code/PKCE flow is documented in [CENT-GOOGLE-AUTHENTICATION.md](CENT-GOOGLE-AUTHENTICATION.md). Backend changes do not enable GIS in a deployed environment. The frontend must implement the callback integration below before the operator selects GIS.

## Audit and isolation

The initial working tree already contained edits to `CENT.md`, `backend/Cent.Tests/CentApiTests.cs`, `backend/Cent.Tests/CentGoogleAuthTests.cs`, and `docs/CENT-GOOGLE-AUTHENTICATION.md`. Those files are preserved without edits by this implementation. Initial status, diff, and tracked-file hashes were captured under the ignored `.dotnet/cent-gis-audit/` directory before implementation.

Cent owns `backend/Cent.Module/`, `backend/Cent.Tests/`, `backend/Cent.Preview/`, its `cent_*` tables, and Cent-specific documentation. The shared host, solution, build files and dependency configuration, `Flashcards.*` projects, and Lerona migrations/authentication are outside the implementation scope. No shared file, package reference, version, Lerona source/test, database model, or migration is changed. The Cent registration and routes already present in the shared host are sufficient.

Existing behavior reviewed: the opt-in `Cent:Enabled` registration, environment-variable provider, Cent CORS and admission/rate limits, Google code exchange and validation, five-minute hashed attempts, conditional-delete replay prevention, case-sensitive unique `GoogleSubject`, unique normalized email, account conflict/race handling, password login, seven-day opaque session issuance, revocation, separate authentication scheme, and Cent migration history. Both Google flows now call the same existing subject-to-account/session logic. Existing subject associations, user IDs, roles, profiles, purchases, entitlements, and sessions are retained. Matching email alone never links accounts. A new Google identity receives a STUDENT account with no password.

Files changed by this task:

- `backend/Cent.Module/CentGoogleAuth.cs`: mode selection and reuse of the existing account/session logic.
- `backend/Cent.Module/CentModule.cs`: Cent-only service and named HTTP client registrations.
- `backend/Cent.Module/CentGoogleGisAuth.cs` (new): configuration discovery, challenges, browser/origin proof and GIS endpoints.
- `backend/Cent.Module/CentGoogleIdTokenValidator.cs` (new): JWT validation and fixed-endpoint certificate cache.
- `backend/Cent.Tests/CentGoogleGisTests.cs` (new): GIS security, compatibility and cache regression tests.
- `docs/CENT-GOOGLE-GIS.md` (new): this contract, audit and operator instructions.

## Configuration

The sole new setting is `Cent__Google__Mode` (`Cent:Google:Mode` in .NET). Exact, case-sensitive values:

| Value | Behavior |
| --- | --- |
| Absent, empty, or `AuthorizationCode` | Existing `/auth/google/start` and `/auth/google/complete` behavior; GIS endpoints return `503 google_unavailable`. |
| `GisIdToken` | GIS challenge/completion enabled; old start/complete return `503 google_unavailable`. Their implementation remains available by changing the mode back. |
| Any other value | Google endpoints fail closed with `503 google_unavailable`. Password login and existing sessions continue to work. |

`Cent__Google__Enabled=true` and a nonempty Google Web Client ID are also required. GIS does not read or require a client secret, redirect URI, code, access token, or refresh token. Existing secret/redirect settings may remain in the environment for rollback; GIS ignores them. `Cent__Google__Enabled=false` disables both Google modes.

Required Liara values (placeholders must be replaced by the existing operator-managed values):

```dotenv
Cent__Enabled=true
Cent__Google__Enabled=true
Cent__Google__Mode=GisIdToken
Cent__Google__ClientId=<EXISTING_GOOGLE_WEB_CLIENT_ID.apps.googleusercontent.com>
Cent__AllowedOrigins__0=https://itatest.ir
```

Preserve the existing Cent database connection and other Cent/Lerona settings. `ConnectionStrings__CentDb` is the optional existing Cent connection; when absent, the existing FlashcardsDb fallback still applies. Do not print connection strings or secrets. No live values were inspected or changed.

These existing URL settings remain unchanged and have distinct meanings:

```dotenv
Cent__ClientBaseUrl=https://itatest.ir
Cent__PublicApiBaseUrl=https://lerona-app.com
```

`PublicApiBaseUrl` is the **server origin**, without `/api/cent/v1`; existing payments append their callback path. The frontend's API base is `https://lerona-app.com/api/cent/v1`. Neither setting supplies the Google audience.

Local Cent.Preview process environment (PowerShell):

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Cent__Enabled = 'true'
$env:Cent__Google__Enabled = 'true'
$env:Cent__Google__Mode = 'GisIdToken'
$env:Cent__Google__ClientId = '<EXISTING_GOOGLE_WEB_CLIENT_ID.apps.googleusercontent.com>'
$env:Cent__AllowedOrigins__0 = 'http://localhost:5173'
dotnet run --project backend/Cent.Preview/Cent.Preview.csproj --no-launch-profile
```

Preview already defaults to `http://127.0.0.1:5108`, sets its client base to `http://localhost:5173/cent-s`, uses its own local SQLite database and disallows Production. A fresh preview database includes the Google schema. If an older local preview database lacks it, its existing explicit `--upgrade-google-auth=true` command-line option backs up that local SQLite database and adds the existing Google schema; this is not a production command. For a shared-host local run instead, preserve its existing required Lerona environment/configuration; no shared settings are added here. Cent settings must be process/hosting environment variables: the existing repository `.env` provider imports only SMS/OTP settings.

Google's existing Web Client must allow the frontend's exact JavaScript origin (`https://itatest.ir` or `http://localhost:5173`). Those external settings were not inspected or changed. No Google callback URL is used by the GIS JavaScript callback mode. For rollback, preserve the existing code-flow callbacks `https://itatest.ir/auth/google/callback` and `http://localhost:5173/cent-s/auth/google/callback` plus the backend's existing client secret.

## API locations and transport

| Environment | Frontend | API base |
| --- | --- | --- |
| Production | `https://itatest.ir` | `https://lerona-app.com/api/cent/v1` |
| Local | `http://localhost:5173/cent-s/` | `http://127.0.0.1:5108/api/cent/v1` |

Every path below is relative to the API base. URLs have no `/cent-s` API prefix. Responses use camelCase JSON. All GIS/discovery responses reaching Cent handlers use `Cache-Control: private, no-store, max-age=0`; do not cache them in frontend/service-worker/CDN code. The inherited Cent authentication rate limit is **10 requests per minute per observed remote IP**, shared with other `/auth` endpoints, including discovery and both Google modes. The existing Cent admission limit is four simultaneous requests per process. A trusted proxy must continue supplying the existing correct remote IP behavior; otherwise users may share one IP budget. Limits are process-local, as before.

For both GIS POST endpoints:

```http
Content-Type: application/json
Origin: https://itatest.ir
X-Cent-GIS: 1
```

Use local Origin `http://localhost:5173` in development. Browsers supply Origin automatically; frontend JavaScript must not try to set it. The Origin must exactly equal a configured `Cent:AllowedOrigins` entry. HTTP loopback is accepted only when the backend environment is Development. Missing, `null`, untrusted, multiple, or mismatched Origin values fail. A valid `X-Cent-GIS: 1` is mandatory. Both POST endpoints accept JSON only, with an optional charset parameter; forms are rejected. Use `fetch(..., { credentials: 'omit', ... })`. No cookie or existing Authorization header is needed.

The custom header and JSON trigger CORS preflight. Existing Cent CORS permits these headers and the configured origins, without credentialed cookies. CORS is not the only check: GIS explicitly rejects disallowed Origins. An attacker site cannot read a challenge proof or make a simple form POST to complete sign-in.

This API supports **GIS JavaScript callback / popup / One Tap** integration. It does not implement Google's automatic `login_uri` form POST. The documented Google double-submit-cookie check applies to that automatic form integration; this cross-origin JSON API instead enforces Origin, a non-simple custom-header request, and the server-issued browser proof. Do not direct a GIS `login_uri` to these endpoints or submit a `g_csrf_token` form.

### 1. Discover mode

```http
GET /api/cent/v1/auth/google/config
Origin: https://itatest.ir
```

No body, custom header, cookie, or bearer token. An allowed Origin is required for a browser to read the CORS response, but this public discovery handler itself does not reject requests without Origin.

`200` when GIS is configured:

```json
{
  "enabled": true,
  "mode": "GisIdToken",
  "clientId": "<GOOGLE_WEB_CLIENT_ID.apps.googleusercontent.com>"
}
```

When the existing code flow is configured, `mode` is `AuthorizationCode` and `clientId` is its configured public client ID. When Google is disabled, `enabled` is false, `clientId` is null, and `mode` still reports the selected valid mode (default `AuthorizationCode`). Unknown modes or incomplete enabled-mode configuration return `503 google_unavailable`; do not silently switch flows. This endpoint never returns credentials or redirect URLs. Do not poll it continuously because it shares the authentication rate limit.

### 2. Create challenge

```http
POST /api/cent/v1/auth/google/gis/challenge
Content-Type: application/json
Origin: https://itatest.ir
X-Cent-GIS: 1

{}
```

The body must be exactly an empty JSON object, apart from whitespace, with a maximum of **256 UTF-8 bytes**. Unknown fields, duplicate fields, a null/array body, empty content, or invalid JSON return 400.

`200` response:

```json
{
  "challengeId": "<43_CHARACTER_BASE64URL_CHALLENGE_ID>",
  "nonce": "<43_CHARACTER_BASE64URL_NONCE>",
  "browserBinding": "<43_CHARACTER_BASE64URL_BROWSER_PROOF>",
  "expiresAt": "<UTC_ISO_8601_TIMESTAMP>"
}
```

The three strings are independent server-generated 32-byte cryptographically random values, encoded as canonical unpadded base64url: exactly 43 characters from `A-Z a-z 0-9 - _`. Placeholder text above is descriptive and must not be sent literally. Expiration is five minutes after creation according to server UTC. The frontend should discard the attempt before `expiresAt`; server time is authoritative.

Keep `challengeId`, `nonce`, `browserBinding`, and expiry together in memory for this attempt, or in tab-scoped `sessionStorage` if necessary. Treat `browserBinding` as a secret proof: never put it in a URL, shared storage, logs, analytics, Google options, or another browser. Never use the nonce as the browser proof. The server stores only domain-separated SHA-256 hashes of ID and origin-bound proof, the nonce hash, and expiry in `cent_google_attempts`. A proof from one allowed frontend origin fails at another allowed origin. Expired rows are cleaned up when either Google flow starts a new attempt.

### 3. Obtain the credential from GIS

Use the discovery response's `clientId` as GIS `client_id` and the challenge's exact `nonce` as GIS `nonce`. Configure JavaScript `callback` and popup UX, without `login_uri`. The callback's `response.credential` is the `idToken` sent below. Only initialize the current GIS attempt once; cancel/discard an earlier prompt before replacing its nonce. On retry start a new challenge and reconfigure the current attempt consistently. Do not reuse an earlier credential for a new challenge.

The ID token must contain a verified email and Google subject. Backend validation uses RS256 only, Google signing keys, exact client audience, one of Google's two accepted issuers, strict expiration and optional not-before, issued-at no more than 30 seconds in the future and before expiration, exact challenge nonce, bounded nonempty subject, valid email, boolean `email_verified=true`, and `azp` equal to the client ID whenever present. Unsupported algorithms, duplicate payload/header claims, and critical header extensions are rejected. Decoded claims are interpreted only after signature verification. Backend configuration of Client ID remains authoritative.

### 4. Complete authentication

```http
POST /api/cent/v1/auth/google/gis/complete
Content-Type: application/json
Origin: https://itatest.ir
X-Cent-GIS: 1

{
  "challengeId": "<EXACT_CHALLENGE_ID_FROM_STEP_2>",
  "browserBinding": "<EXACT_BROWSER_PROOF_FROM_STEP_2>",
  "idToken": "<GIS_CALLBACK_RESPONSE_CREDENTIAL>"
}
```

All three fields are required, non-null strings with the exact camelCase names shown. Unknown/duplicate properties are rejected. ID and proof must be canonical 43-character base64url values; `idToken` must be nonblank and at most 10,000 characters. The entire JSON body is limited to **16,384 UTF-8 bytes**, including escaping and whitespace. Limits also apply to streamed requests without Content-Length. Maximum JSON depth is four. There is no nonce field in completion: the server compares the signed token's nonce with the stored hash.

A live matching ID, origin and proof is consumed with an atomic conditional database delete **before** key retrieval/token validation. Exactly one concurrent completion can proceed. Invalid ID, expired ID, wrong origin-bound proof, consumed ID, or lost concurrent race returns the same `401 invalid_google_credentials`. Wrong proof does not consume another browser's real challenge. Once the correct proof consumes an attempt, token errors, account conflicts, key-service failure, cancellation, and lost responses all require a fresh challenge.

`200` response (identical shape and session mechanism to Cent password/code login):

```json
{
  "token": "<64_CHARACTER_UPPERCASE_HEX_CENT_SESSION_TOKEN>",
  "expiresAt": "<UTC_ISO_8601_TIMESTAMP_SEVEN_DAYS_AFTER_LOGIN>",
  "user": {
    "id": "<CENT_USER_UUID>",
    "name": "<CENT_PROFILE_NAME>",
    "email": "<CENT_PROFILE_EMAIL>",
    "role": "STUDENT"
  }
}
```

Existing users retain their actual existing role, which may differ from STUDENT. Existing profile data is not overwritten by Google email/name changes. Store/use the returned Cent session with the frontend's existing Cent session handling; clear the challenge/proof and Google credential immediately. Never use the Google ID token as a Cent bearer token. No Google token is stored in the database or intentionally logged by these handlers.

Authenticated Cent calls use:

```http
Authorization: Bearer <CENT_SESSION_TOKEN>
```

`GET /api/cent/v1/me` returns the Cent user. `POST /api/cent/v1/auth/logout` with that bearer token returns 204 and revokes that session immediately. Sessions expire after seven days and have no new refresh protocol. Logout does not revoke other sessions or delete user data. A Cent session does not authenticate Lerona APIs.

### Error and retry contract

Application errors have this shape; message text is for display and must not be used as an error identifier:

```json
{
  "message": "<SAFE_HUMAN_READABLE_MESSAGE>",
  "code": "invalid_google_credentials"
}
```

| HTTP | `code` | Meaning and frontend action |
| --- | --- | --- |
| 400 | `invalid_request` | Malformed/duplicate/unknown JSON fields, wrong body/field types or formats, missing fields, oversized token string. Fix request; discard current attempt and start again. |
| 401 | `invalid_google_credentials` | Challenge/proof/expiry/replay/concurrency rejection or unverifiable/invalid signed claims/signature. Discard attempt and token; start a new challenge and new GIS interaction. |
| 403 | `google_origin_rejected` | Missing/untrusted Origin, production loopback HTTP Origin, or missing/incorrect `X-Cent-GIS: 1`. Fix integration/configuration; do not loop retries. |
| 409 | `google_account_conflict` | Email already belongs to a different Cent identity/password account. Challenge is consumed. Use the existing Cent sign-in method; no automatic linking. |
| 413 | `request_too_large` | Challenge/completion request exceeds its byte limit. Fix serialization; do not resend the oversized body. |
| 415 | `unsupported_media_type` | Content-Type is not `application/json`. Fix request. |
| 404 | No guaranteed JSON body/code | `Cent__Enabled` is false, or the deployed backend predates these endpoints; GIS routes are absent. Check backend version/configuration. |
| 429 | No guaranteed JSON body/code | Production shared-host rate limiter or Cent admission limiter. Respect `Retry-After` when present; otherwise wait at least 60 seconds. If a completion may have reached the server, start a new attempt after waiting. |
| 503 (local Cent.Preview only) | Empty body, no code | Preview retains the framework default for authentication rate-limit rejection, with no Retry-After; wait at least 60 seconds. Production uses 429 and Retry-After. This existing host difference is preserved. |
| 503 | `google_unavailable` | Google mode disabled/misconfigured or certificate retrieval/parse/validation unavailable. After configuration/network recovery and a delay, start a fresh challenge; never replay the old completion. |
| 503 | `temporarily_unavailable` | Existing Cent infrastructure/DB failure handler. Respect Retry-After if present; otherwise wait at least five seconds and start fresh when recovered. |

Browser CORS rejection may prevent JavaScript reading a 403 body for a disallowed origin; report integration failure instead of trying to parse hidden content. Proxy/server rejection or aborted connections may have no JSON response at all. Do not treat a network error as evidence the challenge was unused. No idempotent replay/retrieval of a previously issued session is provided. The certificate-service failure backoff is five seconds; `google_unavailable` does not promise a Retry-After header. Invalid configuration will keep returning 503 until corrected. Avoid automatic repeated Google prompts or request storms.

## Public keys and production connectivity

GIS fetches only this fixed endpoint, without redirects:

```text
https://www.googleapis.com/oauth2/v1/certs
```

It is Google's PEM certificate endpoint linked in the official verification guide. `Microsoft.IdentityModel` / `JwtSecurityTokenHandler` performs signature/algorithm/issuer/audience/lifetime validation using the certificate's RSA public key; there is no custom signature implementation. Existing IdentityModel assemblies already arrive through Cent's installed dependencies; no dependency or version changes are made. No JWT global/static settings are changed.

Certificate responses are capped at 65,536 bytes, 16 certificates, 16,384 characters per PEM, and bounded key IDs. Certificates must be currently valid and use RSA keys of at least 2048 bits. The HTTPS endpoint is fixed; JWT `jku`/`x5u` cannot change it. Fetches, including reading the body, have a ten-second cancellation deadline and use normal system TLS validation.

The Cent singleton cache honors `Cache-Control: max-age`, conservatively subtracts Age/apparent response age, and caps reuse at six hours and the earliest certificate expiry. Missing max-age defaults to one minute; no-store/no-cache/zero-age responses are used only for the current fetch. Expired keys are never used as a fallback. Concurrent refreshes are serialized. An unknown `kid` can refresh a still-valid cache at most once per 30 seconds; a newly rotated key may fail closed during that short cooldown, so retry using a new challenge after waiting. Successful refresh replaces the key set. A failed refresh uses a five-second retry backoff and returns 503; already-cached known keys remain usable only until their original expiry. Missing keys after a successful fetch return 401.

The untouched existing code-flow validator (`Google.Apis.Auth` 1.73.0) fetches JWK keys from **`https://www.googleapis.com/oauth2/v3/certs`** and has its own one-hour cache/explicit refresh behavior. That flow still exchanges authorization codes at `https://oauth2.googleapis.com/token`. GIS never invokes that client, token exchange, tokeninfo, discovery endpoints, or the v3 endpoint.

The operator reported HTTP 200 for v1 certs from Liara, and token exchange timeouts including IPv4. This implementation did not run inside Liara, so it does not independently verify production connectivity, response bodies, CA trust, or continuing reachability.

Exact noncredentialed commands for the **operator to run inside the Liara container**:

```sh
# GIS production dependency: test full GET, not just HEAD; inspect status, headers and PEM JSON.
curl --fail-with-body --show-error --silent --connect-timeout 10 --max-time 20 --dump-header - 'https://www.googleapis.com/oauth2/v1/certs'

# Same endpoint via IPv4, if dual-stack routing needs diagnosis.
curl -4 --fail-with-body --show-error --silent --connect-timeout 10 --max-time 20 --dump-header - 'https://www.googleapis.com/oauth2/v1/certs'

# Existing authorization-code validator only; not required by GIS.
curl -4 --fail-with-body --show-error --silent --connect-timeout 10 --max-time 20 --dump-header - 'https://www.googleapis.com/oauth2/v3/certs'
```

No secret, OAuth code, ID token, or session should be inserted into connectivity commands. Successful certificate downloads alone do not establish that the full browser GIS integration works.

## Database and deployment

No model change or new migration is required. GIS reuses `cent_google_attempts` with a domain-separated challenge hash and an origin-bound proof hash in the existing verifier column. It reuses `cent_users.GoogleSubject` and its unique case-sensitive index and `cent_sessions`. All replicas arbitrate replay through the existing database, without sticky sessions, cookies, or new data-protection keys. Expired attempts are cleaned up on subsequent starts; there is no new background job.

The existing Cent migration `20261109184203_AddCentGoogleAuthentication` must already be installed (GoogleSubject/index/attempt table), as required for the current code flow. Database schema presence in production was not queried. If it is missing, separately review the **existing Cent-only migration** under the operator's normal database process; do not run shared migrations or assume this change applies any migration. No production database command was run.

Operator deployment sequence:

1. Review the Cent-only diff and test results. Keep the existing code-flow config while preparing the backend build.
2. Test the v1 endpoint from the actual Liara app/container using the commands above; confirm HTTP 200 with valid certificate JSON, not an HTML/proxy response.
3. Prepare the frontend against the exact API contract in this document and verify Google Web Client JavaScript origins. Frontend implementation and external settings are outside this backend change.
4. Build/publish using the repository's existing production procedure. No changes to host/package/deployment infrastructure are needed. Confirm the existing Cent Google schema before rollout. Do not auto-apply production migrations.
5. When frontend and backend are ready, the operator selects `Cent__Google__Mode=GisIdToken` with the required Cent settings and restarts through the normal process. No client secret/redirect change is necessary.
6. Verify `/auth/google/config`, CORS preflight, then sign in with a real Google account from `https://itatest.ir`. Check repeat login retains the Cent user, password login works, bearer `/me` works, and logout revokes the session. Observe status/error counts without recording tokens or bodies.

No deployment, environment edit, cloud change, commit, or push was performed by this implementation.

Rollback: set `Cent__Google__Mode=AuthorizationCode` (or remove it), retain/restore the existing `Cent__Google__ClientSecret` and `Cent__Google__RedirectUri`, and use the previous frontend code-flow integration. No data rollback is needed and GIS-created GoogleSubject associations/sessions remain compatible. Code flow still needs a working Google token endpoint; rolling back modes cannot repair the reported Liara connectivity failure. Alternatively set `Cent__Google__Enabled=false` to disable Google sign-in while preserving password authentication and current sessions. A normal previous-backend rollback is schema-compatible because there is no model change.

## Local verification

Start Cent.Preview with the environment above, then inspect discovery:

```sh
curl --fail-with-body --show-error --silent 'http://127.0.0.1:5108/api/cent/v1/auth/google/config'
curl --fail-with-body --show-error --silent -X POST 'http://127.0.0.1:5108/api/cent/v1/auth/google/gis/challenge' -H 'Origin: http://localhost:5173' -H 'X-Cent-GIS: 1' -H 'Content-Type: application/json' --data '{}'
```

A real sign-in needs a frontend GIS callback configured with the returned client ID/nonce and a real Google credential. Synthetic placeholder JWTs must fail. Do not paste a real ID token or proof into shell history, issue trackers or logs. The included tests run against synthetic locally signed tokens and intercepted certificate HTTP, with an in-memory Cent database; they need no Google credentials or production network/database access.

```powershell
dotnet test backend/Cent.Tests/Cent.Tests.csproj --no-restore
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --no-restore
dotnet build backend/Cent.Module/Cent.Module.csproj --no-restore
dotnet build backend/Cent.Preview/Cent.Preview.csproj --no-restore
git diff --check
```

Where this checkout uses repository-local NuGet/CLI caches, first set `DOTNET_CLI_HOME` to `.dotnet` and `NUGET_PACKAGES` to `.nuget/packages` as absolute paths. Tests cover real RSA signatures and claims, registration/repeat login, existing password/account conflicts, exact environment bindings, browser proof/origin/CSRF, bounded requests, atomic replay/concurrency, certificate failures/caching/rotation, and the absence of token-exchange calls. The existing code-flow, password and Lerona regression suites remain intact.

Remaining external checks: actual Liara environment values and certificate connectivity, production Cent schema, Google Cloud origins/client configuration, real GIS browser credential delivery, frontend handling of cancellation/expiry/storage/retries, and existing proxy IP attribution. The restrictive connection to the token endpoint is bypassed by design for GIS; browser access to Google GIS and backend access to v1 certificates are still required. A blocked certificate endpoint or an outage beyond cache expiry fails closed.

## Verification results (2026-11-16)

- Full Cent suite: **108 passed, 0 failed, 0 skipped** (10 minutes 27 seconds). Includes all 47 existing cases and 61 new GIS cases; existing code-flow/password/database constraint and migration-model checks pass.
- Unmodified Lerona suite: **396 passed, 0 failed, 3 skipped** (399 total; 9 minutes 33 seconds). The three `MobileCloudFlowTests` require the optional `LERONA_FRONTEND_DIR` real frontend/backend regression setup, which was not configured.
- Cent.Module and Cent.Preview builds pass. The standalone builds report zero warnings/errors.
- `git diff --check` passes. SHA-256 comparison of tracked files against the initial snapshot identifies only the two Cent.Module files listed above as changed by this task. All four pre-existing edited files are byte-for-byte preserved. The four new source/documentation files are also exclusively Cent-specific. No shared or Lerona file changed.
- Existing full-test build warnings remain: NU1510 package pruning, cached NU1900 vulnerability-feed access warnings, and MSB3277 EF Core 10.0.11/10.0.13 conflicts. No unrelated package or configuration changes were made to address them.
- Local test logs, TRX reports and isolation snapshots are under the ignored `.dotnet/cent-gis-audit/` directory. Synthetic test certificates/tokens were used; no real Google sign-in, Liara command, production schema query, deployment, or production data mutation was performed.

## Official references

- [Google: verify the Google ID token on your server](https://developers.google.com/identity/gsi/web/guides/verify-google-id-token) (trusted signature validation, issuer/audience/expiry, key caching and subject-based identity).
- [Google GIS JavaScript reference](https://developers.google.com/identity/gsi/web/reference/js-reference) (callback, credential and nonce options).
- [Google.Apis.Auth 1.73.0 validator source](https://github.com/googleapis/google-api-dotnet-client/blob/v1.73.0/Src/Support/Google.Apis.Auth/GoogleJsonWebSignature.cs) and [endpoint constants](https://github.com/googleapis/google-api-dotnet-client/blob/v1.73.0/Src/Support/Google.Apis.Auth/OAuth2/GoogleAuthConsts.cs) (the existing v3 validator behavior).
- [Microsoft JWT validator source](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/7.7.1/src/System.IdentityModel.Tokens.Jwt/JwtSecurityTokenHandler.cs) (library-managed cryptographic verification; no custom cryptographic validation).
