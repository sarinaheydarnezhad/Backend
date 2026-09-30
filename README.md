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

The initial migration creates the six data tables and a SQL Server trigger that rejects updates/deletes to review events. The second migration adds nullable account credentials for existing users, a filtered unique email index, sessions and hashed refresh tokens. All foreign keys use no cascade deletion. Archive decks/cards instead of deleting history. Migrations are not applied automatically on API startup. Tests use in-memory SQLite for relational CRUD and constraints and validate the SQL Server migration script without contacting SQL Server; applying the migrations to a real SQL Server requires a running server and a configured connection string.

`FlashcardsQueries` exposes async scoped reads for decks by user, cards by deck, review state, review history, and user changes after a UTC timestamp. The changes query includes archived rows. It is an unpaged data-layer primitive; cursor and paging semantics belong to a later sync feature.
