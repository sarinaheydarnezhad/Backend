# Cent deployment files

`cent-google-auth.sql` is the idempotent Google authentication delta for databases with InitialCent and Curriculum already applied. See [Cent Google authentication](../../../docs/CENT-GOOGLE-AUTHENTICATION.md) for migration ordering and environment configuration. It is not applied automatically.

The SQL script in this directory is generated for `CentDbContext` only. It adds `cent_` objects and the independent `cent_migrations_history` table. It does not run automatically or alter Lerona's migration history. See the repository's `CENT.md` for setup and activation.

`cent-initial.sql` is the original initial migration. `cent-curriculum.sql` is the current idempotent script including both the initial and additive Curriculum migrations. Review and back up the intended database before applying it. This workspace has imported curriculum only into the local SQLite preview, not a deployed SQL Server.
