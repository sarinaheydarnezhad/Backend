# Cent deployment files

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
Actual production migration history was deliberately not inspected. Do not infer
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
