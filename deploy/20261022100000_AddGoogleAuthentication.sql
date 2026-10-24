BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022100000_AddGoogleAuthentication'
)
BEGIN
    ALTER TABLE [Users] ADD [GoogleSubject] varchar(255) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022100000_AddGoogleAuthentication'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Users_GoogleSubject] ON [Users] ([GoogleSubject]) WHERE [GoogleSubject] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022100000_AddGoogleAuthentication'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261022100000_AddGoogleAuthentication', N'10.0.11');
END;

COMMIT;
GO
