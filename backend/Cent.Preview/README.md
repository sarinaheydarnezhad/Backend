# Cent local preview

This development host runs the same `Cent.Module` used by the shared Lerona API, with a local SQLite database and simulated payments. All required source and package references are inside the backend repository. No workspace tools directory is needed.

From the backend repository root:

```powershell
dotnet run --project backend/Cent.Preview/Cent.Preview.csproj
```

- API: http://127.0.0.1:5108/api/cent/v1/status
- Swagger: http://127.0.0.1:5108/swagger/index.html
- Local administrator: `admin@cent.local` / `cent-local-preview-only`

The launch profile sets Development. The host refuses to start in other environments. Use `Flashcards.Api` for the shared production backend.

After updating to Google authentication, existing preview databases require the explicit, backed-up upgrade:

```powershell
dotnet run --project backend/Cent.Preview/Cent.Preview.csproj -- --upgrade-google-auth true
```

Fresh previews create the current schema automatically. See [Cent Google authentication](../../docs/CENT-GOOGLE-AUTHENTICATION.md) for the environment variables, Google Cloud setup and frontend callback contract.

In the frontend repository, run:

```powershell
$env:CENT_BACKEND_URL = "http://127.0.0.1:5108"
npm.cmd ci
npm.cmd run dev
```

Open http://localhost:5173/cent-s/. Local state is stored in this project's `App_Data/preview.db`. Git ignores that directory, `bin/` and `obj/`; it includes the source, project file and launch profile. A fresh clone creates its database and local administrator automatically.

To preserve another local preview database once, use:

```powershell
dotnet run --project backend/Cent.Preview/Cent.Preview.csproj -- --import-preview-db "PATH_TO_EXISTING_PREVIEW_DB"
```

Import uses SQLite's online backup API and refuses to overwrite an existing destination. Omit the import flag on subsequent starts. Changing `--urls` is supported for isolated verification.

To upgrade an existing local preview and load the supplied curriculum once:

```powershell
dotnet run --project backend/Cent.Preview/Cent.Preview.csproj -- --upgrade-curriculum true --import-curriculum true
```

The explicit development upgrade backs up SQLite to a timestamped `.bak`, adds curriculum tables and columns, and preserves existing users, purchases, notes and results. Import verifies bundled source checksums and is idempotent. Omit both flags on subsequent starts. These preview operations do not apply SQL Server migrations or touch a deployed database.

For `Source checksum mismatch` or a missing source file, see
[`Cent.Curriculum/README.md`](../Cent.Curriculum/README.md) for UTF-8-aware
verification, Windows Git checkout repair, and the source-generation commands.
