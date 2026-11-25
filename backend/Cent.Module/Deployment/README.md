# Cent deployment files

## Upgrade the audited production database

The 25 November 2026 read-only audit found the first three Cent migrations applied, through `20261109184203_AddCentGoogleAuthentication`. Use [cent-production-upgrade.sql](../../../deploy/cent-production-upgrade.sql) to apply the six later migrations through `20261124174931_ReaderProtectionBudgets`. The script uses only Cent tables and migration history. Password sign-in and reset reuse the existing password hash column and the SMS migration schema.

1. Back up the SQL Server database and confirm you can restore it. Production currently hosts Lerona tables in the same database.
2. In SSMS, select the intended database, enable **Query → SQLCMD Mode**, and run the entire script. The script stops on error and requires the three baseline Cent migrations. Each pending migration runs in its own transaction and the script can be rerun after a successful or partial upgrade.
3. Verify that `dbo.cent_migrations_history` contains all nine entries listed by the repository and ends with `20261124174931_ReaderProtectionBudgets`. Then run `powershell -NoProfile -ExecutionPolicy Bypass -File deploy/Deploy-Backend.ps1` from the backend repository root. This stages `Flashcards.Api` with its dependencies and preserves the backup-key disk mount. Deploying the repository root directly lets Liara discover utility projects such as `Lerona.SmsCheck`.
4. Keep the production Google OAuth settings until the deployed GIS sign-in has been verified.

## Earlier migration review files

Local audit, 23 November 2026: these files are review inputs, not an automatic
deployment chain. The complete current Cent migration order is InitialCent,
Curriculum, AddCentGoogleAuthentication, VersionedMockExams, VersionedCurriculum,
SubjectCommerce, AddCentSmsAuthentication. The checked-in scripts here cover
different ranges; in particular there is no standalone SubjectCommerce script.
The SMS delta is `deploy/cent-sms-auth.sql` at the repository root.

Generate and review a complete idempotent script from the current Cent model
offline before any separately authorized production work. The offline regression
test `ProductionPrerequisiteTests` verifies all seven migrations, the model snapshot,
Cent-only objects and independent history, without opening a SQL Server connection.
That earlier review did not inspect production migration history. Do not infer
which migrations are applied from this workspace's SQLite preview.

Production prerequisites include an explicit Cent connection, backup/recovery
plan, SQL Server rehearsal (including locking/concurrency), independent Cent
provider secrets, exact Google/CORS origins, HTTPS/CSP, account recovery, content
review and offer approval. See `docs/CENT-AUTHENTICATION.md` for current identity
and bootstrap behavior. Local fake payments and fixtures establish no live-provider
or academic acceptance. Frontend and backend remain independently configurable.

`cent-google-auth.sql` is the idempotent Google authentication delta for databases with InitialCent and Curriculum already applied. See [Cent Google authentication](../../../docs/CENT-GOOGLE-AUTHENTICATION.md) for migration ordering and environment configuration. It is not applied automatically.

The SQL script in this directory is generated for `CentDbContext` only. It adds `cent_` objects and the independent `cent_migrations_history` table. It does not run automatically or alter Lerona's migration history. See the repository's `CENT.md` for setup and activation.

`cent-initial.sql` is the original initial migration. `cent-curriculum.sql` includes only the initial and additive Curriculum migrations; it is not the complete current schema. Review and back up the intended database before any separately authorized application. This workspace has imported curriculum only into the local SQLite preview, not a deployed SQL Server.
