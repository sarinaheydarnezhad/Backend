BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261109184203_AddCentGoogleAuthentication'
)
BEGIN
    ALTER TABLE [cent_users] ADD [GoogleSubject] varchar(255) COLLATE Latin1_General_100_BIN2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261109184203_AddCentGoogleAuthentication'
)
BEGIN
    CREATE TABLE [cent_google_attempts] (
        [StateHash] varchar(64) NOT NULL,
        [VerifierHash] varchar(64) NOT NULL,
        [NonceHash] varchar(64) NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_cent_google_attempts] PRIMARY KEY ([StateHash])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261109184203_AddCentGoogleAuthentication'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_cent_users_GoogleSubject] ON [cent_users] ([GoogleSubject]) WHERE [GoogleSubject] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261109184203_AddCentGoogleAuthentication'
)
BEGIN
    CREATE INDEX [IX_cent_google_attempts_ExpiresAt] ON [cent_google_attempts] ([ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261109184203_AddCentGoogleAuthentication'
)
BEGIN
    INSERT INTO [cent_migrations_history] ([MigrationId], [ProductVersion])
    VALUES (N'20261109184203_AddCentGoogleAuthentication', N'10.0.11');
END;

COMMIT;
GO

