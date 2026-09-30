# Flashcards backend data layer

The backend is a .NET 10 ASP.NET Core API in [`backend/`](backend/). SQL Server persistence is implemented with EF Core 10. Deck creation remains a placeholder; no endpoint writes data yet.

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
- `POST /api/v1/decks`: validates the request DTO; valid requests return 501 until the deck feature is built.

The URL prefix `/api/v1` is the versioning contract. Later incompatible versions get their own `/api/v2` group and DTOs. Operational routes such as `/health` are not versioned. Request validation uses .NET 10 `AddValidation` and data annotations; failures produce HTTP 400 with validation details. Unexpected errors are logged and returned as RFC ProblemDetails with a `traceId` and no exception text. Logs are JSON on stdout.

## Configuration

Base settings live in `appsettings.json`; environment overrides live in `appsettings.Development.json` and `appsettings.Production.json`. Environment variables override JSON; double underscores represent nested keys. Put secrets in environment variables or a deployment secret store, never committed JSON. Example for a later database-backed feature:

```powershell
$env:ConnectionStrings__FlashcardsDb = 'Server=localhost;Database=Flashcards;Integrated Security=True;TrustServerCertificate=True'
$env:Cors__AllowedOrigins__0 = 'https://app.example.com'
```

Development defaults to Windows SQL Server LocalDB (`FlashcardsDev`); override `ConnectionStrings__FlashcardsDb` for another server. Production has no connection string in source control: supply it via environment configuration or a secret store. EF Core registers the context and scoped query service only when a connection string is configured. `Cors:AllowedOrigins` defaults to an empty list in production. Set explicit origins for deployed clients. Swagger UI and the OpenAPI route are enabled in Development only.

## Database updates

Install the EF Core 10 CLI (`dotnet tool install --global dotnet-ef --version 10.0.11`), set `ConnectionStrings__FlashcardsDb` to the target SQL Server connection string, and run from the repository root:

```powershell
dotnet ef database update --project backend/Flashcards.Infrastructure --startup-project backend/Flashcards.Infrastructure
```

For a reviewed deployment, generate an idempotent SQL script instead:

```powershell
dotnet ef migrations script --idempotent --project backend/Flashcards.Infrastructure --startup-project backend/Flashcards.Infrastructure --output migration.sql
```

The initial migration creates all six tables, relational constraints, indexes, and a SQL Server trigger that rejects updates/deletes to review events. Review events also reject tracked changes in the DbContext; all foreign keys use no cascade deletion. Archive decks/cards instead of deleting history. The migration is not applied automatically on API startup. Tests use in-memory SQLite for relational CRUD and constraint checks and validate the SQL Server migration script without contacting SQL Server; applying the migration to a real SQL Server requires a running server and a configured connection string.

`FlashcardsQueries` exposes async scoped reads for decks by user, cards by deck, review state, review history, and user changes after a UTC timestamp. The changes query includes archived rows. It is an unpaged data-layer primitive; cursor and paging semantics belong to a later sync feature.
