BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110145342_RequireVerifiedPhoneAuthentication'
)
BEGIN
    ALTER TABLE [Users] ADD [PhoneVerifiedAtUtc] datetimeoffset(7) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110145342_RequireVerifiedPhoneAuthentication'
)
BEGIN
    ALTER TABLE [Users] ADD [VerifiedPhoneNumber] varchar(16) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110145342_RequireVerifiedPhoneAuthentication'
)
BEGIN
    ALTER TABLE [OtpChallenges] ADD [ActivationUserId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110145342_RequireVerifiedPhoneAuthentication'
)
BEGIN
    UPDATE s SET [RevokedAtUtc] = SYSUTCDATETIME()
    FROM [AuthSessions] AS s
    INNER JOIN [Users] AS u ON u.[Id] = s.[UserId]
    WHERE s.[RevokedAtUtc] IS NULL AND u.[PhoneVerifiedAtUtc] IS NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261110145342_RequireVerifiedPhoneAuthentication'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261110145342_RequireVerifiedPhoneAuthentication', N'10.0.13');
END;

COMMIT;
GO

