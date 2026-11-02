BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261102105010_AddSm2ReviewSystem'
)
BEGIN
    ALTER TABLE [ReviewEvents] ADD [SchedulerJson] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261102105010_AddSm2ReviewSystem'
)
BEGIN
    ALTER TABLE [Decks] ADD [ReviewSystem] varchar(16) NOT NULL DEFAULT 'leitner';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261102105010_AddSm2ReviewSystem'
)
BEGIN
    ALTER TABLE [CardReviewStates] ADD [SchedulerStateJson] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261102105010_AddSm2ReviewSystem'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261102105010_AddSm2ReviewSystem', N'10.0.13');
END;

COMMIT;
GO

