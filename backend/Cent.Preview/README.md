# Cent local preview

This development host runs the same `Cent.Module` used by the shared Lerona API, with a local SQLite database and simulated payments. All required source and package references are inside the backend repository. No workspace tools directory is needed.

From the backend repository root:

```powershell
dotnet run --project backend/Cent.Preview/Cent.Preview.csproj
```

- API: http://127.0.0.1:5108/api/cent/v1/status
- Swagger: http://127.0.0.1:5108/swagger/index.html
- Authentication uses the existing Cent GIS/SMS configuration. There is no seeded
  administrator or password login. Never restore the retired password routes for tests.

The launch profile sets Development. The host refuses to start in other environments. Use `Flashcards.Api` for the shared production backend.

After updating to Google authentication, existing preview databases require the explicit, backed-up upgrade:

```powershell
dotnet run --project backend/Cent.Preview/Cent.Preview.csproj -- --upgrade-google-auth true
```

For the current GIS and SMS schema, existing previews also need the explicit additive, backed-up `--upgrade-sms-auth true` upgrade. This adds the Cent SMS tables and the GIS linking column; it does not reset accounts or learning data. Run the older Google upgrade first if that schema is missing. Fresh isolated previews create the current schema automatically. Production uses the separate Cent SQL Server migration, not these preview upgrades. See [Cent authentication](../../docs/CENT-AUTHENTICATION.md) for configuration, contracts, migration and recovery details.

In the frontend repository, run:

```powershell
$env:CENT_BACKEND_URL = "http://127.0.0.1:5108"
npm.cmd ci
npm.cmd run dev
```

Open http://localhost:5173/cent-s/. Local state is stored in this project's `App_Data/preview.db`. Git ignores that directory, `bin/` and `obj/`; it includes the source, project file and launch profile. A fresh clone creates its database, without a default administrator.

To preserve another local preview database once, use:

```powershell
dotnet run --project backend/Cent.Preview/Cent.Preview.csproj -- --import-preview-db "PATH_TO_EXISTING_PREVIEW_DB"
```

Import uses SQLite's online backup API and refuses to overwrite an existing destination. Omit the import flag on subsequent starts. Changing `--urls` is supported for isolated verification.

To upgrade an existing local preview and load the supplied curriculum once:

```powershell
dotnet run --project backend/Cent.Preview/Cent.Preview.csproj -- --upgrade-curriculum true --import-curriculum true --prepare-subject-offers true --exit-after-setup true
```

Stop the local preview before maintenance. The explicit development upgrade makes
an exclusively created `.bak`, verifies its integrity and foreign keys before DDL,
adds missing current-model tables/indexes and legacy curriculum columns, and widens
the legacy product constraint to accept `SUBJECT`. SQLite requires a transactional
product-table copy for that constraint: all columns/rows, custom indexes/triggers
and references are preserved and checked before commit. The database file is never
reset or replaced. Existing users, purchases, notes, progress and results remain.

Import verifies bundled source checksums, preserves existing IDs and publication
choices, and is idempotent. Offer preparation adds disabled review-gated products;
it does not certify content or enable sales. Exam seeds stay disabled for new starts.
`--exit-after-setup true` exits before binding a port. Omit maintenance flags on
subsequent normal starts. These operations do not execute SQL Server migrations.

For fake purchase/browser tests, use a **new** disposable database under
`App_Data/isolated`, `--admin-fixtures true`, `--import-curriculum true`,
`--prepare-subject-offers true` and `--sandbox-subject-offers true` on another
loopback port. Fixtures are written beside that isolated database. Never use
fixture sessions as evidence of a real provider login. Sandbox approval is refused
for the existing preview database. Enable the optional mock-exam routes locally
with `Cent__Exams__Enabled=true`; content activation still requires its own review.

The Google HTTP client inherits the process/system proxy configuration. Keep
Psiphon settings outside source and credentials out of frontend environment files.

For `Source checksum mismatch` or a missing source file, see
[`Cent.Curriculum/README.md`](../Cent.Curriculum/README.md) for UTF-8-aware
verification, Windows Git checkout repair, and the source-generation commands.
