# Flashcards backend foundation

The backend is a .NET 10 ASP.NET Core API in [`backend/`](backend/). This phase establishes the host, contracts, persistence wiring, and integration tests. Deck creation is a placeholder; no deck data is stored yet.

## Projects

| Project | Purpose | References |
| --- | --- | --- |
| `Flashcards.Domain` | Domain model boundary (empty until the first feature) | None |
| `Flashcards.Application` | Use case contracts and application services | Domain |
| `Flashcards.Infrastructure` | EF Core 10 SQL Server wiring | Application |
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

EF Core configures the SQL Server provider only when `ConnectionStrings:FlashcardsDb` exists; no migrations or entities have been added. `Cors:AllowedOrigins` defaults to an empty list in production. Set explicit origins for deployed clients. Swagger UI and the OpenAPI route are enabled in Development only. Set `ASPNETCORE_ENVIRONMENT=Production` in the deployed host and provide all production secrets through configuration.
