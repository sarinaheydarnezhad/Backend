IF OBJECT_ID(N'[cent_migrations_history]') IS NULL
BEGIN
    CREATE TABLE [cent_migrations_history] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK_cent_migrations_history] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_audit_events] (
        [Id] uniqueidentifier NOT NULL,
        [ActorId] uniqueidentifier NOT NULL,
        [Action] nvarchar(64) NOT NULL,
        [EntityId] uniqueidentifier NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_cent_audit_events] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_questions] (
        [Id] uniqueidentifier NOT NULL,
        [Subject] nvarchar(24) NOT NULL,
        [Topic] nvarchar(120) NOT NULL,
        [Difficulty] nvarchar(16) NOT NULL,
        [Text] nvarchar(max) NOT NULL,
        [OptionsJson] nvarchar(max) NOT NULL,
        [CorrectAnswer] int NOT NULL,
        [Explanation] nvarchar(max) NOT NULL,
        [Kind] nvarchar(16) NOT NULL,
        [Active] bit NOT NULL,
        CONSTRAINT [PK_cent_questions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_trainings] (
        [Id] uniqueidentifier NOT NULL,
        [Title] nvarchar(200) NOT NULL,
        [Description] nvarchar(max) NOT NULL,
        [IncludedInSubscription] bit NOT NULL,
        [Published] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_cent_trainings] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_users] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(120) NOT NULL,
        [Email] nvarchar(256) NOT NULL,
        [NormalizedEmail] nvarchar(256) NOT NULL,
        [PasswordHash] nvarchar(512) NOT NULL,
        [Role] nvarchar(16) NOT NULL,
        [PurchaseVersion] bigint NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_cent_users] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_documents] (
        [Id] uniqueidentifier NOT NULL,
        [TrainingId] uniqueidentifier NOT NULL,
        [Title] nvarchar(200) NOT NULL,
        [SortOrder] int NOT NULL,
        [PageCount] int NOT NULL,
        [Published] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_cent_documents] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_documents_cent_trainings_TrainingId] FOREIGN KEY ([TrainingId]) REFERENCES [cent_trainings] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_products] (
        [Id] uniqueidentifier NOT NULL,
        [Title] nvarchar(200) NOT NULL,
        [Kind] nvarchar(16) NOT NULL,
        [TrainingId] uniqueidentifier NULL,
        [PriceRial] bigint NOT NULL,
        [DurationDays] int NULL,
        [Active] bit NOT NULL,
        CONSTRAINT [PK_cent_products] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_cent_product_access] CHECK (([Kind] = 'TRAINING' AND [TrainingId] IS NOT NULL AND [DurationDays] IS NULL) OR ([Kind] = 'SUBSCRIPTION' AND [TrainingId] IS NULL AND [DurationDays] BETWEEN 1 AND 366)),
        CONSTRAINT [CK_cent_product_price] CHECK ([PriceRial] > 0),
        CONSTRAINT [FK_cent_products_cent_trainings_TrainingId] FOREIGN KEY ([TrainingId]) REFERENCES [cent_trainings] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_attempts] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [RequestId] uniqueidentifier NOT NULL,
        [Kind] nvarchar(16) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [SubmittedAt] datetimeoffset NULL,
        [ResultJson] nvarchar(max) NULL,
        CONSTRAINT [PK_cent_attempts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_attempts_cent_users_UserId] FOREIGN KEY ([UserId]) REFERENCES [cent_users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_sessions] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [TokenHash] varchar(64) NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [RevokedAt] datetimeoffset NULL,
        CONSTRAINT [PK_cent_sessions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_sessions_cent_users_UserId] FOREIGN KEY ([UserId]) REFERENCES [cent_users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_document_pages] (
        [DocumentId] uniqueidentifier NOT NULL,
        [Number] int NOT NULL,
        [Image] varbinary(max) NOT NULL,
        [Width] int NOT NULL,
        [Height] int NOT NULL,
        CONSTRAINT [PK_cent_document_pages] PRIMARY KEY ([DocumentId], [Number]),
        CONSTRAINT [FK_cent_document_pages_cent_documents_DocumentId] FOREIGN KEY ([DocumentId]) REFERENCES [cent_documents] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_document_sections] (
        [Id] uniqueidentifier NOT NULL,
        [DocumentId] uniqueidentifier NOT NULL,
        [Title] nvarchar(200) NOT NULL,
        [StartPage] int NOT NULL,
        CONSTRAINT [PK_cent_document_sections] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_document_sections_cent_documents_DocumentId] FOREIGN KEY ([DocumentId]) REFERENCES [cent_documents] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_notes] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [DocumentId] uniqueidentifier NOT NULL,
        [Page] int NOT NULL,
        [AnchorY] float NOT NULL,
        [Body] nvarchar(4000) NOT NULL,
        [Revision] bigint NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_cent_notes] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_notes_cent_documents_DocumentId] FOREIGN KEY ([DocumentId]) REFERENCES [cent_documents] ([Id]),
        CONSTRAINT [FK_cent_notes_cent_users_UserId] FOREIGN KEY ([UserId]) REFERENCES [cent_users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_study_progress] (
        [UserId] uniqueidentifier NOT NULL,
        [DocumentId] uniqueidentifier NOT NULL,
        [LastPage] int NOT NULL,
        [FurthestPage] int NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_cent_study_progress] PRIMARY KEY ([UserId], [DocumentId]),
        CONSTRAINT [FK_cent_study_progress_cent_documents_DocumentId] FOREIGN KEY ([DocumentId]) REFERENCES [cent_documents] ([Id]),
        CONSTRAINT [FK_cent_study_progress_cent_users_UserId] FOREIGN KEY ([UserId]) REFERENCES [cent_users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_orders] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [RequestId] uniqueidentifier NOT NULL,
        [Title] nvarchar(200) NOT NULL,
        [Kind] nvarchar(16) NOT NULL,
        [TrainingId] uniqueidentifier NULL,
        [DurationDays] int NULL,
        [AmountRial] bigint NOT NULL,
        [Status] nvarchar(24) NOT NULL,
        [Provider] nvarchar(16) NOT NULL,
        [Authority] varchar(100) NULL,
        [Reference] varchar(100) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [PaidAt] datetimeoffset NULL,
        CONSTRAINT [PK_cent_orders] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_orders_cent_products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [cent_products] ([Id]),
        CONSTRAINT [FK_cent_orders_cent_trainings_TrainingId] FOREIGN KEY ([TrainingId]) REFERENCES [cent_trainings] ([Id]),
        CONSTRAINT [FK_cent_orders_cent_users_UserId] FOREIGN KEY ([UserId]) REFERENCES [cent_users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_ai_analyses] (
        [Id] uniqueidentifier NOT NULL,
        [AttemptId] uniqueidentifier NOT NULL,
        [Provider] nvarchar(24) NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_cent_ai_analyses] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_ai_analyses_cent_attempts_AttemptId] FOREIGN KEY ([AttemptId]) REFERENCES [cent_attempts] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_attempt_questions] (
        [AttemptId] uniqueidentifier NOT NULL,
        [Position] int NOT NULL,
        [QuestionId] uniqueidentifier NOT NULL,
        [SnapshotJson] nvarchar(max) NOT NULL,
        [Answer] int NULL,
        CONSTRAINT [PK_cent_attempt_questions] PRIMARY KEY ([AttemptId], [Position]),
        CONSTRAINT [FK_cent_attempt_questions_cent_attempts_AttemptId] FOREIGN KEY ([AttemptId]) REFERENCES [cent_attempts] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_entitlements] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [TrainingId] uniqueidentifier NULL,
        [Kind] nvarchar(16) NOT NULL,
        [StartsAt] datetimeoffset NOT NULL,
        [ExpiresAt] datetimeoffset NULL,
        [RevokedAt] datetimeoffset NULL,
        CONSTRAINT [PK_cent_entitlements] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_entitlements_cent_orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [cent_orders] ([Id]),
        CONSTRAINT [FK_cent_entitlements_cent_trainings_TrainingId] FOREIGN KEY ([TrainingId]) REFERENCES [cent_trainings] ([Id]),
        CONSTRAINT [FK_cent_entitlements_cent_users_UserId] FOREIGN KEY ([UserId]) REFERENCES [cent_users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE TABLE [cent_payment_events] (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [Action] nvarchar(24) NOT NULL,
        [Code] int NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_cent_payment_events] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_payment_events_cent_orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [cent_orders] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_ai_analyses_AttemptId] ON [cent_ai_analyses] ([AttemptId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_attempts_UserId_RequestId] ON [cent_attempts] ([UserId], [RequestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_audit_events_CreatedAt] ON [cent_audit_events] ([CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_document_sections_DocumentId_StartPage] ON [cent_document_sections] ([DocumentId], [StartPage]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_documents_TrainingId_SortOrder] ON [cent_documents] ([TrainingId], [SortOrder]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_entitlements_OrderId] ON [cent_entitlements] ([OrderId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_entitlements_TrainingId] ON [cent_entitlements] ([TrainingId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_entitlements_UserId_Kind_TrainingId] ON [cent_entitlements] ([UserId], [Kind], [TrainingId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_notes_DocumentId] ON [cent_notes] ([DocumentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_notes_UserId_DocumentId_Page] ON [cent_notes] ([UserId], [DocumentId], [Page]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_cent_orders_Authority] ON [cent_orders] ([Authority]) WHERE [Authority] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_orders_ProductId] ON [cent_orders] ([ProductId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_cent_orders_Provider_Reference] ON [cent_orders] ([Provider], [Reference]) WHERE [Reference] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_orders_TrainingId] ON [cent_orders] ([TrainingId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_orders_UserId_RequestId] ON [cent_orders] ([UserId], [RequestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_payment_events_OrderId_CreatedAt] ON [cent_payment_events] ([OrderId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_products_TrainingId] ON [cent_products] ([TrainingId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_questions_Active_Kind] ON [cent_questions] ([Active], [Kind]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_sessions_TokenHash] ON [cent_sessions] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_sessions_UserId_ExpiresAt] ON [cent_sessions] ([UserId], [ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE INDEX [IX_cent_study_progress_DocumentId] ON [cent_study_progress] ([DocumentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_users_NormalizedEmail] ON [cent_users] ([NormalizedEmail]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107101801_InitialCent'
)
BEGIN
    INSERT INTO [cent_migrations_history] ([MigrationId], [ProductVersion])
    VALUES (N'20261107101801_InitialCent', N'10.0.13');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[cent_questions]') AND [c].[name] = N'Topic');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [cent_questions] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [cent_questions] ALTER COLUMN [Topic] nvarchar(500) NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_questions] ADD [FigureSvg] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_questions] ADD [ImportKey] nvarchar(300) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_questions] ADD [Origin] nvarchar(32) NOT NULL DEFAULT N'manual';
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_questions] ADD [SourceExerciseId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_questions] ADD [SourceSectionId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_questions] ADD [TopicId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_documents] ADD [SourceId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_attempts] ADD [AssessmentMode] nvarchar(36) NOT NULL DEFAULT N'overview';
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_attempts] ADD [CourseCodesJson] nvarchar(max) NOT NULL DEFAULT N'[]';
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_attempts] ADD [DurationMinutes] int NOT NULL DEFAULT 60;
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE TABLE [cent_courses] (
        [Code] nvarchar(24) NOT NULL,
        [Title] nvarchar(256) NOT NULL,
        [EnglishTitle] nvarchar(256) NOT NULL,
        [SortOrder] int NOT NULL,
        [TrainingId] uniqueidentifier NULL,
        [Active] bit NOT NULL,
        CONSTRAINT [PK_cent_courses] PRIMARY KEY ([Code]),
        CONSTRAINT [FK_cent_courses_cent_trainings_TrainingId] FOREIGN KEY ([TrainingId]) REFERENCES [cent_trainings] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE TABLE [cent_curriculum_imports] (
        [PackageHash] varchar(64) NOT NULL,
        [ImportedAt] datetimeoffset NOT NULL,
        [SourceCount] int NOT NULL,
        [TopicCount] int NOT NULL,
        [QuestionCount] int NOT NULL,
        CONSTRAINT [PK_cent_curriculum_imports] PRIMARY KEY ([PackageHash])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE TABLE [cent_sources] (
        [Id] uniqueidentifier NOT NULL,
        [ImportKey] nvarchar(300) NOT NULL,
        [CourseCode] nvarchar(24) NOT NULL,
        [FileName] nvarchar(300) NOT NULL,
        [Title] nvarchar(500) NOT NULL,
        [OriginalTitle] nvarchar(500) NOT NULL,
        [Sha256] varchar(64) NOT NULL,
        [OriginalHtml] nvarchar(max) NOT NULL,
        [PlainText] nvarchar(max) NOT NULL,
        [ImportedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_cent_sources] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_sources_cent_courses_CourseCode] FOREIGN KEY ([CourseCode]) REFERENCES [cent_courses] ([Code])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE TABLE [cent_chapters] (
        [Id] uniqueidentifier NOT NULL,
        [ImportKey] nvarchar(300) NOT NULL,
        [CourseCode] nvarchar(24) NOT NULL,
        [SourceId] uniqueidentifier NOT NULL,
        [Title] nvarchar(500) NOT NULL,
        [SortOrder] int NOT NULL,
        CONSTRAINT [PK_cent_chapters] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_chapters_cent_courses_CourseCode] FOREIGN KEY ([CourseCode]) REFERENCES [cent_courses] ([Code]),
        CONSTRAINT [FK_cent_chapters_cent_sources_SourceId] FOREIGN KEY ([SourceId]) REFERENCES [cent_sources] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE TABLE [cent_topics] (
        [Id] uniqueidentifier NOT NULL,
        [ImportKey] nvarchar(300) NOT NULL,
        [ChapterId] uniqueidentifier NOT NULL,
        [CourseCode] nvarchar(24) NOT NULL,
        [Title] nvarchar(500) NOT NULL,
        [EnglishTitle] nvarchar(256) NOT NULL,
        [SortOrder] int NOT NULL,
        [Foundation] bit NOT NULL,
        CONSTRAINT [PK_cent_topics] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_topics_cent_chapters_ChapterId] FOREIGN KEY ([ChapterId]) REFERENCES [cent_chapters] ([Id]),
        CONSTRAINT [FK_cent_topics_cent_courses_CourseCode] FOREIGN KEY ([CourseCode]) REFERENCES [cent_courses] ([Code])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE TABLE [cent_source_sections] (
        [Id] uniqueidentifier NOT NULL,
        [SourceId] uniqueidentifier NOT NULL,
        [TopicId] uniqueidentifier NULL,
        [ImportKey] nvarchar(300) NOT NULL,
        [Heading] nvarchar(500) NOT NULL,
        [SortOrder] int NOT NULL,
        [Text] nvarchar(max) NOT NULL,
        [Html] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_cent_source_sections] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_source_sections_cent_sources_SourceId] FOREIGN KEY ([SourceId]) REFERENCES [cent_sources] ([Id]),
        CONSTRAINT [FK_cent_source_sections_cent_topics_TopicId] FOREIGN KEY ([TopicId]) REFERENCES [cent_topics] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE TABLE [cent_source_exercises] (
        [Id] uniqueidentifier NOT NULL,
        [SourceSectionId] uniqueidentifier NOT NULL,
        [TopicId] uniqueidentifier NULL,
        [ImportKey] nvarchar(300) NOT NULL,
        [Text] nvarchar(max) NOT NULL,
        [OptionsJson] nvarchar(max) NOT NULL,
        [OriginalCorrectAnswer] int NULL,
        [Solution] nvarchar(max) NOT NULL,
        [Status] nvarchar(32) NOT NULL,
        [ReviewNote] nvarchar(4000) NULL,
        CONSTRAINT [PK_cent_source_exercises] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_source_exercises_cent_source_sections_SourceSectionId] FOREIGN KEY ([SourceSectionId]) REFERENCES [cent_source_sections] ([Id]),
        CONSTRAINT [FK_cent_source_exercises_cent_topics_TopicId] FOREIGN KEY ([TopicId]) REFERENCES [cent_topics] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_cent_questions_ImportKey] ON [cent_questions] ([ImportKey]) WHERE [ImportKey] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_questions_SourceExerciseId] ON [cent_questions] ([SourceExerciseId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_questions_SourceSectionId] ON [cent_questions] ([SourceSectionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_questions_Subject_Active_TopicId] ON [cent_questions] ([Subject], [Active], [TopicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_questions_TopicId] ON [cent_questions] ([TopicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_documents_SourceId] ON [cent_documents] ([SourceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_chapters_CourseCode] ON [cent_chapters] ([CourseCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_chapters_ImportKey] ON [cent_chapters] ([ImportKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_chapters_SourceId] ON [cent_chapters] ([SourceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_courses_TrainingId] ON [cent_courses] ([TrainingId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_source_exercises_ImportKey] ON [cent_source_exercises] ([ImportKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_source_exercises_SourceSectionId] ON [cent_source_exercises] ([SourceSectionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_source_exercises_TopicId] ON [cent_source_exercises] ([TopicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_source_sections_ImportKey] ON [cent_source_sections] ([ImportKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_source_sections_SourceId_SortOrder] ON [cent_source_sections] ([SourceId], [SortOrder]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_source_sections_TopicId] ON [cent_source_sections] ([TopicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_sources_CourseCode] ON [cent_sources] ([CourseCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_sources_ImportKey] ON [cent_sources] ([ImportKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_topics_ChapterId] ON [cent_topics] ([ChapterId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE INDEX [IX_cent_topics_CourseCode_ChapterId_SortOrder] ON [cent_topics] ([CourseCode], [ChapterId], [SortOrder]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_topics_ImportKey] ON [cent_topics] ([ImportKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_documents] ADD CONSTRAINT [FK_cent_documents_cent_sources_SourceId] FOREIGN KEY ([SourceId]) REFERENCES [cent_sources] ([Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_questions] ADD CONSTRAINT [FK_cent_questions_cent_source_exercises_SourceExerciseId] FOREIGN KEY ([SourceExerciseId]) REFERENCES [cent_source_exercises] ([Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_questions] ADD CONSTRAINT [FK_cent_questions_cent_source_sections_SourceSectionId] FOREIGN KEY ([SourceSectionId]) REFERENCES [cent_source_sections] ([Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    ALTER TABLE [cent_questions] ADD CONSTRAINT [FK_cent_questions_cent_topics_TopicId] FOREIGN KEY ([TopicId]) REFERENCES [cent_topics] ([Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261107130614_Curriculum'
)
BEGIN
    INSERT INTO [cent_migrations_history] ([MigrationId], [ProductVersion])
    VALUES (N'20261107130614_Curriculum', N'10.0.13');
END;

COMMIT;
GO

