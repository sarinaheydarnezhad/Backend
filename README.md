# Flashcards backend

The backend is a .NET 10 ASP.NET Core API in [`backend/`](backend/) with EF Core 10 SQL Server persistence and email/password accounts. Deck creation remains a placeholder.

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
- `POST /api/v1/decks`: requires a bearer token; valid requests return 501 until the deck feature is built.
- `POST /api/v1/auth/register`, `/login`, `/refresh`: issue access and refresh tokens; duplicate email is 409, invalid credentials/tokens are 401.
- `POST /api/v1/auth/logout`: authenticated; revokes the current session and returns 204.
- `GET /api/v1/me/`, `/me/decks`, `/me/decks/{deckId}/cards`, `/me/cards/{cardId}/review-state`, `/me/cards/{cardId}/review-history`, `/me/settings`: authenticated reads bound to the token subject. API responses exclude credential hashes.
- `POST /api/v1/sync/push` and `GET /api/v1/sync/pull?cursor=0&limit=100`: authenticated, user-scoped offline change exchange. No mobile sync engine is included.

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

`Auth:Issuer` and `Auth:Audience` are configured in `appsettings.json`. `Auth__SigningKey` is **required at startup** and must be a base64-encoded random value of at least 32 bytes. Persist the key securely across restarts and replicas; changing it invalidates outstanding access tokens. Send access tokens via `Authorization: Bearer <token>` over HTTPS. Store refresh tokens in the mobile device's secure credential storage and never log them. Passwords are salted and hashed by ASP.NET Core's password hasher; only SHA-256 hashes of random refresh tokens are stored in SQL Server. Access tokens expire after 15 minutes; refresh sessions have a 30-day absolute lifetime. Refresh rotates the token; reuse of a consumed token revokes its session and invalidates its access tokens. Logout revokes the current session immediately. Existing users without an email and password stay in the database but cannot sign in until account migration is planned.

Authentication endpoints use a per-IP fixed-window limit of 10 requests per minute per API instance, returning 429 when exceeded. Behind a reverse proxy, configure trusted forwarded headers or enforce a shared limit at the edge. Do not forward untrusted client-supplied IP headers as the rate-limit identity.

## Database updates

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
