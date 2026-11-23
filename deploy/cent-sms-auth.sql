BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261123135254_AddCentSmsAuthentication'
)
BEGIN
    ALTER TABLE [cent_google_attempts] ADD [LinkUserId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261123135254_AddCentSmsAuthentication'
)
BEGIN
    CREATE TABLE [cent_mobile_identities] (
        [Phone] varchar(13) NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [VerifiedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_cent_mobile_identities] PRIMARY KEY ([Phone]),
        CONSTRAINT [FK_cent_mobile_identities_cent_users_UserId] FOREIGN KEY ([UserId]) REFERENCES [cent_users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261123135254_AddCentSmsAuthentication'
)
BEGIN
    CREATE TABLE [cent_sms_budgets] (
        [Key] varchar(80) NOT NULL,
        [ResetAt] datetimeoffset NOT NULL,
        [NextAt] datetimeoffset NOT NULL,
        [Count] int NOT NULL,
        CONSTRAINT [PK_cent_sms_budgets] PRIMARY KEY ([Key])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261123135254_AddCentSmsAuthentication'
)
BEGIN
    CREATE TABLE [cent_sms_challenges] (
        [IdHash] varchar(64) NOT NULL,
        [Phone] varchar(13) NOT NULL,
        [Verifier] varchar(64) NOT NULL,
        [LinkUserId] uniqueidentifier NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [ResendAt] datetimeoffset NOT NULL,
        [Attempts] int NOT NULL,
        [Delivered] bit NOT NULL,
        [Consumed] bit NOT NULL,
        CONSTRAINT [PK_cent_sms_challenges] PRIMARY KEY ([IdHash])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261123135254_AddCentSmsAuthentication'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_mobile_identities_UserId] ON [cent_mobile_identities] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261123135254_AddCentSmsAuthentication'
)
BEGIN
    CREATE INDEX [IX_cent_sms_budgets_ResetAt] ON [cent_sms_budgets] ([ResetAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261123135254_AddCentSmsAuthentication'
)
BEGIN
    CREATE INDEX [IX_cent_sms_challenges_ExpiresAt] ON [cent_sms_challenges] ([ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261123135254_AddCentSmsAuthentication'
)
BEGIN
    CREATE INDEX [IX_cent_sms_challenges_Phone] ON [cent_sms_challenges] ([Phone]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261123135254_AddCentSmsAuthentication'
)
BEGIN
    INSERT INTO [cent_migrations_history] ([MigrationId], [ProductVersion])
    VALUES (N'20261123135254_AddCentSmsAuthentication', N'10.0.11');
END;

COMMIT;
GO

