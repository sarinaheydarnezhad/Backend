# Cent deployment files

The SQL script in this directory is generated for `CentDbContext` only. It adds `cent_` objects and the independent `cent_migrations_history` table. It does not run automatically or alter Lerona's migration history. See the repository's `CENT.md` for setup and activation.

`cent-initial.sql` is the original initial migration. `cent-curriculum.sql` is the current idempotent script including both the initial and additive Curriculum migrations. Review and back up the intended database before applying it. This workspace has imported curriculum only into the local SQLite preview, not a deployed SQL Server.
