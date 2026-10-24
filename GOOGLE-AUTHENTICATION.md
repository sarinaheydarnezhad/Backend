# Google sign-in

The mobile app's **Continue with Google** option is temporarily hidden. Its adapter and `POST /api/v1/auth/google` endpoint are retained for later re-enablement and older clients. The endpoint takes `{ "idToken": "<Google ID token>" }`. First use creates an account; later sign-ins use the same verified Google subject. Responses use the existing application access/refresh token contract and session revocation behavior.

## Deploy

- Set backend-only configuration `Google__WebClientId` to the Google OAuth **Web application** client ID used as `EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID` by the mobile app. No Google client secret is required for native ID-token verification.
- Apply migration `20261022100000_AddGoogleAuthentication` before deploying this backend. It adds `Users.GoogleSubject` and a filtered unique index; it does not migrate or merge existing identities.
- Allow outbound HTTPS access to Google's OpenID discovery and signing-key endpoints. Signing keys are cached and refreshed through the existing Microsoft IdentityModel libraries. Unknown keys request a refresh and fail closed; the user may retry after refresh.
- Missing Google configuration or unavailable discovery returns `503 google_unavailable`. Invalid or expired tokens return `401 invalid_google_token`. Existing-email collisions return `409 google_account_conflict`. Requests use the existing auth rate limiter and successful/error handler responses use `Cache-Control: no-store`.

Signatures, RS256 algorithm, issuer, configured audience, lifetime, verified email, and nonempty subject are checked before account lookup. Google account IDs and emails supplied separately by a client are never trusted. No access to Gmail messages is requested.

Existing email/password accounts are never linked automatically. Users with a matching email must use their original login method; authenticated account linking is not implemented. Google `sub` is the stable identity, not the email address. Archived users are rejected.

The migration can be scripted/reviewed with the existing EF tooling before application to SQL Server. Backend tests use local RSA-signed JWTs and substituted discovery keys without contacting Google. They do not verify live consent, Google Cloud setup, production network access, or real Google credentials. See `lm/docs/google-authentication.md` in the companion mobile repository for native build/client setup.
