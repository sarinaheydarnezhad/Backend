# Flashcards backend

Current backup/sync/atomic guest-copy fixes, API integration requirements and actual test results are in `SYNC-RECOVERY-FIX.md`. Existing clients remain compatible; atomic whole-transfer migration requires the additive authenticated `/api/v1/sync/guest-migration` route. For production deployment, run `powershell -NoProfile -ExecutionPolicy Bypass -File deploy/Deploy-Backend.ps1`. It stages current source, explicitly attaches the existing `backup-keys` disk and verifies the live mount. Publishing binaries alone does not preserve a release disk binding. See [backup recovery](deploy/CLOUD-BACKUP-RECOVERY.md).

The backend is a .NET 10 ASP.NET Core API in [`backend/`](backend/) with EF Core 10 SQL Server persistence, Lerona password login with SMS-verified signup/recovery, and sync-aware deck creation.

## Projects

| Project | Purpose | References |
| --- | --- | --- |
| `Flashcards.Domain` | User, deck, card, review and settings entities | None |
| `Flashcards.Application` | Use case contracts and application services | Domain |
| `Flashcards.Infrastructure` | EF Core 10 SQL Server context, queries and migrations | Application |
| `Flashcards.Api` | HTTP endpoints, configuration, and composition root | Application, Infrastructure |
| `Flashcards.Tests` | Host and integration tests | API |

Infrastructure references Application to implement its abstractions; Domain has no dependency on framework or database code. Endpoints use request and response DTOs and contain no domain logic.

## Run locally

Install the .NET 10 SDK, then from the repository root:

```powershell
dotnet build backend/Flashcards.slnx
dotnet test backend/Flashcards.slnx
dotnet run --project backend/Flashcards.Api
```

The launch profile sets `ASPNETCORE_ENVIRONMENT=Development` and listens at `http://localhost:5100`. In development, Swagger UI is at `/swagger` and the generated OpenAPI document is at `/openapi/v1.json`.

## Endpoints and conventions

- `GET /health`: liveness only, without a database dependency.
- `GET /api/v1/status`: a small response DTO to verify API startup.
- `POST /api/v1/decks`: requires a bearer token and `{ "name": "Vocabulary" }`; returns 201 with `{ "id", "name", "syncVersion" }` and a Location header pointing to `/api/v1/me/decks`. Ownership comes exclusively from the authenticated identity. The server generates the deck, device and change UUIDs, applies the default deck settings, and commits the deck, per-user version and sync-feed entry in the existing sync transaction. The owner's next pull includes the deck as an `upsert`; other users cannot see it. This endpoint creates a new deck on each successful call; it does not provide retry idempotency. Use sync push with client-generated IDs for idempotent offline creation.
- Lerona login uses `/api/v1/auth/phone/login` with phone/password. Signup and forgotten-password recovery first use `/phone/otp/request` and `/otp/verify` for purpose-bound proofs, then `/phone/register` or `/phone/password/reset`. OTP verification alone never issues tokens. Email/Google adapters remain disabled; normal password sessions rotate through `/auth/refresh`.
- `POST /api/v1/auth/logout`: authenticated; revokes the current session and returns 204.
- `POST /api/v1/auth/logout/{sessionId}`: authenticated; idempotently revokes only a session owned by the JWT's user. Used after a fresh login to retry offline logout using non-secret IDs; IDs alone never authorize revocation.

- `GET /api/v1/me/`, `/me/decks`, `/me/decks/{deckId}/cards`, `/me/cards/{cardId}/review-state`, `/me/cards/{cardId}/review-history`, `/me/settings`: authenticated reads bound to the token subject. API responses exclude credential hashes.
- `POST /api/v1/sync/push` and `GET /api/v1/sync/pull?cursor=0&limit=100`: authenticated, user-scoped offline change exchange. No mobile sync engine is included.

Token responses additionally expose `sessionId` and `sessionExpiresAtUtc`. Refresh rotation preserves the original absolute session deadline. Authenticated responses use `Cache-Control: no-store`; logout never deletes account decks/cards/history. [Retained server backups](BACKUPS.md) provide authenticated create/status/list/read/download APIs, encrypted SQL snapshots, retention, retry-safe creation, and explicit non-destructive `mergeMissing` restoration. Apply existing migrations and configure a durable protected key-ring directory before enabling backup operations. No new migration is needed for restore. Scheduled physical cleanup is still operationally managed. See `BACKUP-RESTORE-FIX.md` for current findings, verification and the restore-client integration requirements; `BACKUP-VERIFICATION.md` records the earlier creation-only implementation.

## Phone authentication

Returning users sign in with mobile number and password. New users verify an SMS,
create a password, then enter a name. Forgotten passwords use a separate recovery
SMS proof before setting a new password. Reset preserves the account ID and all
learning/purchase data and revokes previous sessions. Google remains Cent-only.
Cent authentication, users, configuration and sessions are independent.

Actual SMS credentials belong in ignored Backend/.env; the API loads only Lerona
SMS/OTP settings from that file. The example file contains placeholders. Host
configuration takes precedence. The pending additive Lerona migrations retain
their committed names but no longer revoke existing password sessions. Existing
password users do not need SMS to sign in, and unrelated legacy records do not
block new verified signup. No migration runs automatically at API startup.

See [setup](docs/KAVENEGAR_OTP_SETUP.md) and the current
[authentication report](docs/PHONE_PASSWORD_OTP_REPORT.md). No real SMS or production
deployment has been performed; both require explicit user approval.

## Account display names

`PUT /api/v1/me/profile` accepts `{ "displayName": "Sara" }` for the authenticated user. It trims the name, requires 1–120 characters, saves the existing `Users.DisplayName` column and returns the updated profile. This endpoint does not require a database migration or an active subscription.

Deploy the updated API and Infrastructure assemblies together before using the mobile name editor. Updating the APK alone does not update the backend, and an old `backend/publish` directory can omit this endpoint even when it exists in source. Preserve the deployment's existing application settings, secrets and Liara configuration when refreshing the publish files.

An unauthenticated `PUT /api/v1/me/profile` with an empty JSON object should return **401**, not **404**. A 404 means the profile route is not available on that deployment; rebuild and redeploy the backend rather than changing the mobile request or bypassing authentication. Local tests cover authenticated saves, Persian names, later logins, invalid names and account isolation.

## Free-tier creation policy

Free accounts are limited to one active (unarchived) deck and 50 active cards in total. Card counts include unarchived cards in archived decks; archiving a deck alone does not free card capacity. Existing oversized datasets are preserved: viewing, exporting, editing and archival remain available, but study and new content require active Plus entitlement or explicitly reducing active data. Nothing is automatically deleted.

`SyncService` checks quotas inside the existing transaction after acquiring the per-user sync-head write lock. Both `POST /api/v1/decks` and sync push creation use the same policy. A limit failure rolls back the full batch, including entity changes, versions and feed entries, and returns HTTP 403 `{ error, code }` with `free_tier_deck_limit` or `free_tier_card_limit`. Counts use the authenticated owner's ID, never client entitlement/owner claims. Guest data is local mobile storage and is not bound/uploaded by signing in.

The backend validates current stored subscriptions through `EntitlementService`; active Plus removes deck/card limits, while unavailable, expired or cancelled plans cannot bypass free limits. Guest migration uses the same checks, with no client-controlled premium exemption. Current verification and deployment requirements are documented in `SYNC-RECOVERY-FIX.md`.

## Sync contract

`push` accepts `{ "changes": [...] }` (1–50 entries, at most 128 KiB per request). Each change has `clientChangeId`, `deviceId`, `entityType` (`deck`, `card`, `reviewState`, `reviewEvent`, `settings`), `entityId`, `operation` (`upsert`, `archive`, or `create` for review events), `clientChangedAtUtc`, `expectedVersion`, and `payload`. Client-generated UUIDs identify entities across devices; settings use the authenticated user's UUID, and review states use their card's UUID. New mutable entities use `expectedVersion: 0`; subsequent upserts/archives must supply the last version returned for **that entity**. Server versions are monotonically increasing per user, not derived from clocks.

The push executes in one transaction. It returns the current `cursor` and each change's `status` (`applied` or `duplicate`) and `serverVersion`. Repeating `(deviceId, clientChangeId)` with the same content is idempotent; changing its content returns 409. An identical review event UUID and payload is also idempotent across operation IDs. Review events are append-only; the server never edits past review events. For mutable entities, a stale `expectedVersion`, archived entity update, or reassigned card deck returns 409 with the attempted payload, current server value, and both versions; the entire push rolls back. Archive is terminal and preserves review history. Unknown or invalid fields return 400. Entity IDs belonging to another user never expose that user's data. There is no automatic conflict resolution.

Each accepted review event advances the server's card review-state projection in server-version order, using the review result and the Leitner intervals. An initial review-state snapshot is accepted only before the first review event; later direct review-state pushes are rejected to prevent stale offline states from replacing combined history. Device clients likewise replay the immutable event stream to derive the displayed state.

`pull` returns up to 100 ordered changes strictly after the provided `cursor`, plus `cursor` (last returned version), `latestVersion`, and `hasMore`. Start at cursor 0 and repeat with the returned cursor until `hasMore` is false. The feed contains full current snapshots for mutable changes, including archives, and original payloads for historical review events. `clientChangedAtUtc` is metadata only; never use device clocks as sync cursors. Preexisting records created outside this API are not automatically backfilled into the feed; migrate/import them explicitly before onboarding an existing account to sync. Direct database writes to synchronized entities must also maintain the feed and versions.

The URL prefix `/api/v1` is the versioning contract. Later incompatible versions get their own `/api/v2` group and DTOs. Operational routes such as `/health` are not versioned. Request validation uses .NET 10 `AddValidation` and data annotations; failures produce HTTP 400 with validation details. Unexpected errors are logged and returned as RFC ProblemDetails with a `traceId` and no exception text. Logs are JSON on stdout.

## Configuration

Base settings live in `appsettings.json`; environment overrides live in `appsettings.Development.json` and `appsettings.Production.json`. Environment variables override JSON; double underscores represent nested keys. Put secrets in environment variables or a deployment secret store, never committed JSON. Example for a later database-backed feature:

```powershell
$env:ConnectionStrings__FlashcardsDb = 'Server=localhost;Database=Flashcards;Integrated Security=True;TrustServerCertificate=True'
$env:Auth__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Cors__AllowedOrigins__0 = 'https://app.example.com'
```

Development defaults to Windows SQL Server LocalDB (`FlashcardsDev`); override `ConnectionStrings__FlashcardsDb` for another server. Production has no connection string in source control: supply it via environment configuration or a secret store. EF Core registers the context and scoped query service only when a connection string is configured. `Cors:AllowedOrigins` defaults to an empty list in production. Set explicit origins for deployed clients. Swagger UI and the OpenAPI route are enabled in Development only.

`Auth:Issuer` and `Auth:Audience` are configured in `appsettings.json`. `Auth__SigningKey` is **required at startup** and must be a base64-encoded random value of at least 32 bytes. Persist the key securely across restarts and replicas; changing it invalidates outstanding access tokens. Send access tokens via `Authorization: Bearer <token>` over HTTPS. Store refresh tokens in the mobile device's secure credential storage and never log them. Existing password hashes continue to authenticate; new and reset passwords use the existing Identity hasher. Only SHA-256 hashes of random refresh tokens are stored in SQL Server. Access tokens expire after 15 minutes; refresh sessions have a 30-day absolute lifetime. Refresh rotates the token; reuse of a consumed token revokes its session and invalidates its access tokens. Logout revokes the current session immediately. Existing mobile/password users can sign in without OTP. SMS is used only for new signup and password recovery.

Authentication endpoints use a per-IP fixed-window limit of 10 requests per minute per API instance, returning 429 when exceeded. Behind a reverse proxy, configure trusted forwarded headers or enforce a shared limit at the edge. Do not forward untrusted client-supplied IP headers as the rate-limit identity.

## Database updates

### Login fails with `Invalid column name 'GoogleSubject'`

The deployed database is behind the API model. SMS OTP authentication still reads preserved legacy metadata in the `User` entity, including `GoogleSubject`; removal of Google authentication does not remove this historical column requirement. Apply the existing `20261022100000_AddGoogleAuthentication` migration to the **same database used by the deployed API**, not just your local development database. Retain this historical migration and apply the later Lerona verification/session migrations in order. No account reset or database recreation is needed.

Back up the database and use your normal controlled migration deployment process. If every earlier migration is already applied, the EF-generated idempotent script `deploy/20261022100000_AddGoogleAuthentication.sql` can be reviewed and run against that database in SQL Server Management Studio. It adds the nullable column, filtered unique index and migration-history entry without changing existing account data. It assumes a normal EF-managed schema; if the migration is already recorded but the column is absent, investigate schema drift instead of deleting history or running a blind repair.

Alternatively, set `ConnectionStrings__FlashcardsDb` to the deployed API's target database in the migration environment and run the `dotnet ef database update` command below; this applies all pending migrations in order. The design-time factory otherwise defaults to local `FlashcardsDev`, so running the command without the correct connection string will not fix a remote deployment. Do not paste production credentials into chat or commit them.

Verify the deployed database after application:

```sql
SELECT DB_NAME() AS CurrentDatabase;
SELECT [MigrationId] FROM [__EFMigrationsHistory]
WHERE [MigrationId] = N'20261022100000_AddGoogleAuthentication';
SELECT COL_LENGTH(N'dbo.Users', N'GoogleSubject') AS GoogleSubjectLength;
```

The migration row should exist and the column length should be `255` (not `NULL`). Retry phone login after successful deployment. The API does not run migrations automatically on startup; restarting it alone does not update the database.

Install the EF Core 10 CLI (`dotnet tool install --global dotnet-ef --version 10.0.11`), set `ConnectionStrings__FlashcardsDb` to the target SQL Server connection string, and run from the repository root:

```powershell
dotnet ef database update --project backend/Flashcards.Infrastructure --startup-project backend/Flashcards.Infrastructure
```

For a reviewed deployment, generate an idempotent SQL script instead:

```powershell
dotnet ef migrations script --idempotent --project backend/Flashcards.Infrastructure --startup-project backend/Flashcards.Infrastructure --output migration.sql
```

The initial migration creates the six data tables and a SQL Server trigger that rejects updates/deletes to review events. The second migration adds nullable account credentials for existing users, a filtered unique email index, sessions and hashed refresh tokens. The third migration adds per-user sync heads, an append-only change log, per-entity versions and an immutability trigger on the log. All foreign keys use no cascade deletion. Archive decks/cards instead of deleting history. Migrations are not applied automatically on API startup. Tests use in-memory SQLite for relational CRUD and constraints and validate the SQL Server migration script without contacting SQL Server; applying the migrations to a real SQL Server requires a running server and a configured connection string.

`FlashcardsQueries` exposes async scoped reads for decks by user, cards by deck, review state, review history, and legacy changes after a UTC timestamp. The sync API instead uses server-issued integer cursors, including archived rows and review events.

## Subscriptions

Subscription catalog and deployment requirements are documented in [SUBSCRIPTIONS.md](SUBSCRIPTIONS.md). See [ACCESS-LIMITS.md](ACCESS-LIMITS.md) for creation/study enforcement, provisional offline access, protected operations and verification. Purchases and Full activation are not available.

## Guest-data copying

The mobile consented guest-copy flow reuses existing authenticated sync receipts, versions, ownership and quota enforcement. The owned review-state read exposes complete Leitner counters and last-review time for final verification. There is no second migration API, destructive title-based merge, quota bypass, backend entity or new EF migration. See `GUEST-MIGRATION.md` for supported data, partial-failure semantics, deployment prerequisites and unverified backend checks.
