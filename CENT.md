# Cent module in the existing Lerona host

Cent is an independent module, enabled only with `Cent__Enabled=true`. `lm` is untouched. Existing Lerona endpoints, domain entities, DbContext, authentication scheme, tables, migrations, backup keys and payment services are unchanged. The API composition root adds Cent service, middleware and route hooks; the release staging script additionally copies `Cent.Module`, which the API now references.

## Isolation

- New routes: `/api/cent/v1/*`.
- Seventeen new data tables, all named `cent_*`, and `cent_migrations_history`.
- Separate DbContext, authentication scheme, users, hashed opaque sessions, administrator role, purchases and access grants. No foreign key refers to a Lerona table.
- No automatic database creation, seeding, migration or background jobs at application startup.
- Cent's admission budget is four requests, before authentication, with no request queue. Lerona's existing global gate still applies. Cent adds separate login/page rate limits and a separate CORS policy.
- Tables share the database's physical resources and backup/restore lifecycle. Prefixes are organizational boundaries, not database permission boundaries.

`ConnectionStrings__CentDb` is optional. If absent, Cent uses the existing `ConnectionStrings__FlashcardsDb` connection. When separating the product later, set CentDb to the new database without changing the module's business code.

## Run the local preview

The Git-included `backend/Cent.Preview` project provides local SQLite storage, simulated payments and Swagger without SQL Server setup. From this repository root:

```powershell
dotnet run --project backend/Cent.Preview/Cent.Preview.csproj
```

Swagger: `http://127.0.0.1:5108/swagger/index.html`. From the frontend repository root, set `$env:CENT_BACKEND_URL = "http://127.0.0.1:5108"` and run `npm.cmd run dev`. Open `http://localhost:5173/cent-s/`.

Local administrator: `admin@cent.local` / `cent-local-preview-only`. Source and launch settings belong to this repository; generated binaries and the local `backend/Cent.Preview/App_Data/` database are ignored. A fresh clone recreates its own local database. The preview does not read another workspace folder and does not run in Production. See `backend/Cent.Preview/README.md` for importing existing local preview data.

## Prepare and apply the additive migration

Build and inspect `backend/Cent.Module/Deployment/cent-initial.sql`. It creates only Cent objects. It is idempotent and uses its own history table; it does not modify `__EFMigrationsHistory`.

```powershell
dotnet restore backend/Flashcards.slnx
dotnet build backend/Flashcards.slnx
dotnet test backend/Cent.Tests/Cent.Tests.csproj
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj

dotnet ef migrations script --idempotent --context CentDbContext --project backend/Cent.Module --startup-project backend/Flashcards.Api --output cent-reviewed.sql
```

Take a normal database backup, then run the reviewed SQL on the intended database using the existing deployment/operator connection. Keep Cent disabled until its tables are present. To apply directly through EF, supply the intended connection through the environment and run:

```powershell
dotnet ef database update --context CentDbContext --project backend/Cent.Module --startup-project backend/Flashcards.Api
```

The design-time factory falls back to a **local CentDev database** if neither connection environment variable is supplied. It intentionally does not read production secrets from publish artifacts. Always identify the target explicitly when applying migrations. Use `--context FlashcardsDbContext` for Lerona's own EF operations if Cent is enabled in the host.

This implementation has not applied migrations to a live database or deployed the server.

## Server configuration

Preserve every existing Lerona environment variable and backup disk binding. Add only the Cent values:

```text
Cent__Enabled=true
Cent__AllowedOrigins__0=https://your-cent-frontend.example
Cent__ClientBaseUrl=https://your-cent-frontend.example/cent-s
Cent__PublicApiBaseUrl=https://your-existing-api.example
Cent__BootstrapToken=<at-least-32-random-characters>
Cent__Payments__MerchantId=<zarinpal-merchant-uuid>
Cent__Payments__Sandbox=true
Cent__MaxContentStorageBytes=536870912
```

`AllowedOrigins` is an origin without a path. `ClientBaseUrl` includes the frontend's base path. `PublicApiBaseUrl` is the API root, without `/api/cent/v1`. Checkout callbacks are fixed by server configuration, never supplied by the browser.

For local tests only, set `ASPNETCORE_ENVIRONMENT=Development` and `Cent__Payments__AllowDevelopmentMock=true`. This cannot enable mock payments in Production. Live payments require a merchant ID, HTTPS URLs, `Sandbox=false`, and an actual sandbox verification before launch. Pending orders retain their original gateway environment. Payment verification uses the stored amount in rials. Codes 100 and 101 are accepted; successful payment state and exactly one access grant commit in one transaction. Subscription renewals are serialized per Cent user.

The content budget defaults to 512 MB of compressed image data. Concurrent uploads can temporarily exceed that admission check by at most the in-flight page bodies; leave resource headroom. Database/index/log overhead is additional. Check actual SQL storage and hosting capacity before increasing this limit.

## Create the first Cent administrator

With Cent enabled and tables applied, call `POST /api/cent/v1/auth/bootstrap` from a trusted operator machine over HTTPS. Use a JSON body containing `email`, `password` (10–128 characters), and `name`, and the header `X-Cent-Bootstrap` containing the server's bootstrap token. Never put secrets in browser build variables, committed files, command-line arguments, logs or screenshots.

The route works only while no Cent administrator exists and the server token is set. Remove `Cent__BootstrapToken` after creating the administrator. Registration always creates STUDENT accounts; a browser-supplied role is ignored. Sessions expire in seven days and logout revokes the server session immediately. Credentials and raw session tokens are never stored in plaintext in the database. There is no public password recovery flow in this initial version; arrange an operator recovery process before a public launch.

## Content and access

A training contains PDF lessons. The admin browser converts each PDF into compressed page images, uploads one page at a time, and publishes only after every page exists. Original PDFs are not stored or exposed. Keep your originals in your own archive.

Each one-time TRAINING product grants permanent online access to that training. A SUBSCRIPTION product grants 1–366 days, covers trainings explicitly marked as included, and unlocks simulator practice. Subscriptions do not renew automatically. Users retain their personal notes after expiry, but page viewing and note editing are checked against current access on every request. Administrators can revoke an entitlement through the authenticated `/admin/entitlements/{id}/revoke` endpoint; this is an access action, not a payment refund.

The reader shows no download/print toolbar, adds personal canvas watermarks, suppresses printing, and blurs content while the tab is hidden. These measures deter casual sharing. They cannot guarantee screenshot prevention or prevent an authorized user from saving displayed image data. Do not market them as DRM or screenshot blocking.

Published pages are immutable. Revisions should be uploaded as new lessons so page-positioned notes remain attached to the version originally studied. Notes use revision checks for concurrent edits, are scoped by authenticated owner, and never render HTML. The application remembers reading position and keeps unsaved note text as a local draft. Account switching clears in-memory user data; drafts are keyed by Cent user/document/page.

The initial practice module stores questions, immutable attempt snapshots, submitted answers, server-computed results and rule-based topic recommendations. Answer keys are omitted from active-attempt responses. Optional external AI and n8n integrations are not enabled by this module.

## Source curriculum and mastery

The bundled curriculum contains 39 original HTML sources from the supplied math, logic, biology, chemistry and physics files, 43 chapters, 190 topics, 666 extracted source exercises and 819 scored questions (331 newly authored). Each scored question has a source section, stable topic ID, answer, explanation and easy/medium/hard label. Original source content is retained. Known answer corrections and one quarantined ambiguous exercise are recorded in `backend/Cent.Module/Content/curriculum.json` under `findings`.

Rebuild source extraction explicitly with `backend/Cent.Curriculum/Extract-Sources.ps1 -InputDirectory <sources-folder>`, then `dotnet run --project backend/Cent.Curriculum`. This regenerates the bundled corpus and review inventory. Apply the additive **Curriculum** EF migration before importing into SQL Server; the development preview has a separate explicit backup/upgrade command in its README. An authenticated Cent administrator imports with `POST /api/cent/v1/admin/curriculum/import`. Import is transactional and repeatable by package hash. It does not set product prices or publish a deployment.

`GET /curriculum` supplies subject/chapter/topic navigation. `POST /attempts/` accepts `kind` (`diagnostic`, `simulator`, `practice`), optional `course` and `topicId`. Sampling spans subjects and topics rather than taking the same first 20 rows. Simulator subscription checks still apply. All submitted practice and assessment results contribute to provisional progress. Source-based lessons use the same purchased-access, page-position, and private-note rules as PDF lessons; their source text is rendered safely as text in the reader.

Curriculum outlines are public and include published source document metadata for the homepage and course catalog. They expose no source text, question options, explanations or answer keys. Lesson-reading and source-section endpoints continue to require authentication and purchased access.

`GET /me/mastery` derives persistent learner progress from saved practice and assessment snapshots and answers (`latest-distinct-10-v2`). For each topic, only the latest submitted answer to each distinct active question counts, with a maximum of 10 distinct questions. At least 3 distinct questions are required for a provisional mastery estimate. Wrong and unanswered questions count in the denominator. A new answer to a repeated question replaces the prior answer without increasing distinct evidence; reading pages alone does not contribute question evidence. The subject's confirmed progress is the equal-weight average over **all imported topics**, with insufficient/unassessed topics contributing zero confirmed progress. Coverage and accuracy on assessed topics are returned separately. For example, three fully correct topics of ten yield 30% confirmed progress; two correct of four yield 50% on that topic. These are provisional estimates over the supplied curriculum, not proof of all knowledge of a subject. Retired questions are excluded from the current progress estimate; historical results remain unchanged.

My Results uses the account's saved server-scored results and current mastery. It shows recent result count, latest score, question-weighted recent accuracy, subject progress/coverage, weak evidenced topics and subject-filtered history. Score changes compare the latest result with a preceding result of the same kind and same subject/topic question counts. Empty and unrelated histories do not receive invented comparison scores. The history endpoint returns up to 100 recent results; mastery derives from all submitted history. Practice is labeled separately from simulator/diagnostic, and all result/progress endpoints remain owner-scoped.

The learner's **تحلیل شخصی** page shows this map with targeted assessment and practice links. The admin question editor supports all five subjects and explicit topic mapping; unmapped manual questions do not contribute to topic mastery.

## Backup, rollback and later extraction

Lerona's **flashcard account backup** feature does not include Cent. A full SQL database backup includes both products and the page-image blobs. Verify that the database backup policy covers these tables and test a restore. Raw source PDF archives need their own backup.

For an immediate application rollback, set `Cent__Enabled=false`; Cent stops serving routes while Lerona continues using its existing configuration. Preserve the `cent_` data. Do not drop tables as an operational rollback after customers have written data.

For later extraction:

1. Deploy the same `Cent.Module` behind a standalone .NET host and apply its migrations to a new SQL Server database.
2. Briefly pause Cent writes, then copy **every** `cent_` table, including image blobs, notes, sessions, payment snapshots, audit records and migration history, retaining GUIDs and timestamps. No Lerona foreign keys need rewriting.
3. Compare row counts and blob checksums, validate purchases/expiry/notes on the new host, and verify backup restore.
4. Move the Cent API URL and payment callback to the new host, update the allowed frontend origin and server URLs, and revoke old sessions if desired. Handle existing pending orders on one authoritative host until verified.
5. Retain the old Cent data until cutover and backups are verified.

## Verification

The existing Lerona regression suite passed (271 passed, one pre-existing skipped live-flow test). Cent's new integration tests use isolated SQLite with UTC tick conversion, plus SQL Server model/script inspection. They do **not** prove SQL Server locking behavior or live gateway connectivity. Run the reviewed migration and concurrent checkout/renewal checks against a staging SQL Server before enabling paid traffic. Browser checks use a temporary local database and an administrator-uploaded sample PDF; no real documents, customer data or payments are used.

The completed verification also includes 13 Cent backend tests (access isolation, expiry, note privacy, immutable published pages, gateway amount/environment handling, answer-key isolation and CORS), a Release publish to a separate local verification directory, the frontend production build and scoring test, and Chrome flows at desktop and mobile widths. Chrome verified PDF conversion/upload, registration, one-time purchase, library/profile, watermarked pages, persistent page notes and recovery of the same session after two temporary API failures. The production npm dependency audit reported zero vulnerabilities; development lint tooling still reports moderate dependency advisories. Existing .NET package/version warnings were not addressed by changing Lerona dependencies.
