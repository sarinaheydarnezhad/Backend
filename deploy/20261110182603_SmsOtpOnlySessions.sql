BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110182603_SmsOtpOnlySessions'
)
BEGIN
    ALTER TABLE [AuthSessions] ADD [OtpAuthenticated] bit NOT NULL DEFAULT CAST(0 AS bit);
END;



IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110182603_SmsOtpOnlySessions'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261110182603_SmsOtpOnlySessions', N'10.0.13');
END;

COMMIT;
GO

