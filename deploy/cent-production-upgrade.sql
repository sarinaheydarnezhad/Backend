:ON ERROR EXIT
-- Select the intended Cent SQL Server database in SSMS and enable SQLCMD Mode.
-- This upgrade starts after AddCentGoogleAuthentication and is safe to rerun.
SET XACT_ABORT ON;
SET NOCOUNT ON;

IF SCHEMA_NAME() <> N'dbo'
    THROW 51000, 'Run this upgrade with dbo as your default schema.', 1;

IF OBJECT_ID(N'dbo.cent_migrations_history', N'U') IS NULL
    THROW 51001, 'The selected database does not contain the Cent migration history.', 1;
GO

IF (
    SELECT COUNT(*) FROM [dbo].[cent_migrations_history]
    WHERE [MigrationId] IN (
        N'20261107101801_InitialCent',
        N'20261107130614_Curriculum',
        N'20261109184203_AddCentGoogleAuthentication'
    )
) <> 3
    THROW 51002, 'One or more required baseline Cent migrations are missing.', 1;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE TABLE [cent_syllabus_revisions] (
        [Id] nvarchar(100) NOT NULL,
        [SourceUrl] nvarchar(2000) NOT NULL,
        [RetrievedAt] datetimeoffset NOT NULL,
        [EvidenceJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_cent_syllabus_revisions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE TABLE [cent_exam_content_releases] (
        [Id] nvarchar(100) NOT NULL,
        [ExamVersionId] nvarchar(100) NOT NULL,
        [SyllabusRevisionId] nvarchar(100) NOT NULL,
        [ManifestJson] nvarchar(max) NOT NULL,
        [ReviewedAt] datetimeoffset NULL,
        [ReviewedBy] uniqueidentifier NULL,
        CONSTRAINT [PK_cent_exam_content_releases] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_exam_content_releases_cent_syllabus_revisions_SyllabusRevisionId] FOREIGN KEY ([SyllabusRevisionId]) REFERENCES [cent_syllabus_revisions] ([Id]),
        CONSTRAINT [FK_cent_exam_content_releases_cent_users_ReviewedBy] FOREIGN KEY ([ReviewedBy]) REFERENCES [cent_users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE TABLE [cent_exam_versions] (
        [Id] nvarchar(100) NOT NULL,
        [ConfigurationRevision] nvarchar(100) NOT NULL,
        [DefinitionJson] nvarchar(max) NOT NULL,
        [Status] nvarchar(24) NOT NULL,
        [AvailableForNewStarts] bit NOT NULL,
        [SyllabusRevisionId] nvarchar(100) NULL,
        [ContentReleaseId] nvarchar(100) NULL,
        [Revision] int NOT NULL,
        CONSTRAINT [PK_cent_exam_versions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_exam_versions_cent_exam_content_releases_ContentReleaseId] FOREIGN KEY ([ContentReleaseId]) REFERENCES [cent_exam_content_releases] ([Id]),
        CONSTRAINT [FK_cent_exam_versions_cent_syllabus_revisions_SyllabusRevisionId] FOREIGN KEY ([SyllabusRevisionId]) REFERENCES [cent_syllabus_revisions] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE TABLE [cent_mock_attempts] (
        [AttemptId] uniqueidentifier NOT NULL,
        [ExamVersionId] nvarchar(100) NOT NULL,
        [ConfigurationRevision] nvarchar(100) NOT NULL,
        [RulesSnapshotJson] nvarchar(max) NOT NULL,
        [Status] nvarchar(24) NOT NULL,
        [Revision] int NOT NULL,
        [ActiveSectionId] uniqueidentifier NULL,
        [ActiveDeadline] datetimeoffset NULL,
        [EffectiveEndedAt] datetimeoffset NULL,
        [FinalizationReason] nvarchar(40) NULL,
        [ResultJson] nvarchar(max) NULL,
        CONSTRAINT [PK_cent_mock_attempts] PRIMARY KEY ([AttemptId]),
        CONSTRAINT [FK_cent_mock_attempts_cent_attempts_AttemptId] FOREIGN KEY ([AttemptId]) REFERENCES [cent_attempts] ([Id]),
        CONSTRAINT [FK_cent_mock_attempts_cent_exam_versions_ExamVersionId] FOREIGN KEY ([ExamVersionId]) REFERENCES [cent_exam_versions] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE TABLE [cent_attempt_sections] (
        [Id] uniqueidentifier NOT NULL,
        [AttemptId] uniqueidentifier NOT NULL,
        [SectionKey] nvarchar(24) NOT NULL,
        [SubjectId] nvarchar(24) NOT NULL,
        [Order] int NOT NULL,
        [QuestionCount] int NOT NULL,
        [DurationSeconds] int NOT NULL,
        [State] nvarchar(24) NOT NULL,
        [ActivatedAt] datetimeoffset NULL,
        [Deadline] datetimeoffset NULL,
        [ClosedAt] datetimeoffset NULL,
        [ProcessedAt] datetimeoffset NULL,
        CONSTRAINT [PK_cent_attempt_sections] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_cent_attempt_section_deadline] CHECK (([ActivatedAt] IS NULL AND [Deadline] IS NULL AND [State] = 'PENDING') OR ([ActivatedAt] IS NOT NULL AND [Deadline] IS NOT NULL)),
        CONSTRAINT [CK_cent_attempt_section_quota] CHECK ([QuestionCount] > 0 AND [DurationSeconds] > 0),
        CONSTRAINT [FK_cent_attempt_sections_cent_mock_attempts_AttemptId] FOREIGN KEY ([AttemptId]) REFERENCES [cent_mock_attempts] ([AttemptId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE TABLE [cent_exam_command_receipts] (
        [AttemptId] uniqueidentifier NOT NULL,
        [CommandId] uniqueidentifier NOT NULL,
        [PayloadHash] varchar(64) NOT NULL,
        [ReceiptJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_cent_exam_command_receipts] PRIMARY KEY ([AttemptId], [CommandId]),
        CONSTRAINT [FK_cent_exam_command_receipts_cent_mock_attempts_AttemptId] FOREIGN KEY ([AttemptId]) REFERENCES [cent_mock_attempts] ([AttemptId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'AvailableForNewStarts', N'ConfigurationRevision', N'ContentReleaseId', N'DefinitionJson', N'Revision', N'Status', N'SyllabusRevisionId') AND [object_id] = OBJECT_ID(N'[cent_exam_versions]'))
        SET IDENTITY_INSERT [cent_exam_versions] ON;
    EXEC(N'INSERT INTO [cent_exam_versions] ([Id], [AvailableForNewStarts], [ConfigurationRevision], [ContentReleaseId], [DefinitionJson], [Revision], [Status], [SyllabusRevisionId])
    VALUES (N''cent-s-2025-2026-r1'', CAST(0 AS bit), N''blueprint-2025-2026-r1'', NULL, N''{"examVersionId":"cent-s-2025-2026-r1","label":"CEnT-S 2025\u20132026","configurationRevision":"blueprint-2025-2026-r1","cycleStartDate":"2025-11-01","cycleEndDateInclusive":"2026-10-31","sections":[{"sectionKey":"math","order":1,"subjectId":"math","questionCount":15,"durationSeconds":1800},{"sectionKey":"logic","order":2,"subjectId":"logic","questionCount":15,"durationSeconds":1800},{"sectionKey":"biology","order":3,"subjectId":"biology","questionCount":10,"durationSeconds":1200},{"sectionKey":"chemistry","order":4,"subjectId":"chemistry","questionCount":10,"durationSeconds":1200},{"sectionKey":"physics","order":5,"subjectId":"physics","questionCount":5,"durationSeconds":600}],"scoring":{"policyId":"raw-quarter-point-v1","correctQuarterPoints":4,"incorrectQuarterPoints":-1,"unansweredQuarterPoints":0},"syllabusRevisionId":null,"contentReleaseId":null,"timingPolicyId":"continuous-v1","submissionPolicyId":null,"totalQuestions":55,"totalDurationSeconds":6600}'', 1, N''PUBLISHED'', NULL),
    (N''cent-s-2026-2027-r1'', CAST(0 AS bit), N''blueprint-2026-2027-r1'', NULL, N''{"examVersionId":"cent-s-2026-2027-r1","label":"CEnT-S 2026\u20132027","configurationRevision":"blueprint-2026-2027-r1","cycleStartDate":"2026-11-01","cycleEndDateInclusive":"2027-10-31","sections":[{"sectionKey":"reading","order":1,"subjectId":"reading","questionCount":10,"durationSeconds":1800},{"sectionKey":"math","order":2,"subjectId":"math","questionCount":15,"durationSeconds":1800},{"sectionKey":"logic","order":3,"subjectId":"logic","questionCount":15,"durationSeconds":1800},{"sectionKey":"biology","order":4,"subjectId":"biology","questionCount":10,"durationSeconds":1200},{"sectionKey":"chemistry","order":5,"subjectId":"chemistry","questionCount":10,"durationSeconds":1200},{"sectionKey":"physics","order":6,"subjectId":"physics","questionCount":5,"durationSeconds":600}],"scoring":{"policyId":"raw-quarter-point-v1","correctQuarterPoints":4,"incorrectQuarterPoints":-1,"unansweredQuarterPoints":0},"syllabusRevisionId":null,"contentReleaseId":null,"timingPolicyId":"continuous-v1","submissionPolicyId":null,"totalQuestions":65,"totalDurationSeconds":8400}'', 1, N''PUBLISHED'', NULL)');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'AvailableForNewStarts', N'ConfigurationRevision', N'ContentReleaseId', N'DefinitionJson', N'Revision', N'Status', N'SyllabusRevisionId') AND [object_id] = OBJECT_ID(N'[cent_exam_versions]'))
        SET IDENTITY_INSERT [cent_exam_versions] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_cent_attempt_sections_active] ON [cent_attempt_sections] ([AttemptId]) WHERE [State] = ''ACTIVE''');
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_attempt_sections_AttemptId_Order] ON [cent_attempt_sections] ([AttemptId], [Order]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_attempt_sections_AttemptId_SectionKey] ON [cent_attempt_sections] ([AttemptId], [SectionKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE INDEX [IX_cent_exam_content_releases_ReviewedBy] ON [cent_exam_content_releases] ([ReviewedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE INDEX [IX_cent_exam_content_releases_SyllabusRevisionId] ON [cent_exam_content_releases] ([SyllabusRevisionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_exam_versions_ConfigurationRevision] ON [cent_exam_versions] ([ConfigurationRevision]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE INDEX [IX_cent_exam_versions_ContentReleaseId] ON [cent_exam_versions] ([ContentReleaseId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE INDEX [IX_cent_exam_versions_SyllabusRevisionId] ON [cent_exam_versions] ([SyllabusRevisionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE INDEX [IX_cent_mock_attempts_ExamVersionId] ON [cent_mock_attempts] ([ExamVersionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    CREATE INDEX [IX_cent_mock_attempts_Status_ActiveDeadline] ON [cent_mock_attempts] ([Status], [ActiveDeadline]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261120134304_VersionedMockExams'
)
BEGIN
    INSERT INTO [cent_migrations_history] ([MigrationId], [ProductVersion])
    VALUES (N'20261120134304_VersionedMockExams', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122081723_VersionedCurriculum'
)
BEGIN
    CREATE TABLE [cent_curriculum_revisions] (
        [Id] nvarchar(100) NOT NULL,
        [SyllabusRevisionId] nvarchar(100) NOT NULL,
        [PackageHash] varchar(64) NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_cent_curriculum_revisions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_curriculum_revisions_cent_syllabus_revisions_SyllabusRevisionId] FOREIGN KEY ([SyllabusRevisionId]) REFERENCES [cent_syllabus_revisions] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122081723_VersionedCurriculum'
)
BEGIN
    CREATE TABLE [cent_practice_content] (
        [AttemptId] uniqueidentifier NOT NULL,
        [ReleaseId] nvarchar(100) NOT NULL,
        [PassagesJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_cent_practice_content] PRIMARY KEY ([AttemptId]),
        CONSTRAINT [FK_cent_practice_content_cent_attempts_AttemptId] FOREIGN KEY ([AttemptId]) REFERENCES [cent_attempts] ([Id]),
        CONSTRAINT [FK_cent_practice_content_cent_curriculum_revisions_ReleaseId] FOREIGN KEY ([ReleaseId]) REFERENCES [cent_curriculum_revisions] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122081723_VersionedCurriculum'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_curriculum_revisions_PackageHash] ON [cent_curriculum_revisions] ([PackageHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122081723_VersionedCurriculum'
)
BEGIN
    CREATE INDEX [IX_cent_curriculum_revisions_SyllabusRevisionId] ON [cent_curriculum_revisions] ([SyllabusRevisionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122081723_VersionedCurriculum'
)
BEGIN
    CREATE INDEX [IX_cent_practice_content_ReleaseId] ON [cent_practice_content] ([ReleaseId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122081723_VersionedCurriculum'
)
BEGIN
    INSERT INTO [cent_migrations_history] ([MigrationId], [ProductVersion])
    VALUES (N'20261122081723_VersionedCurriculum', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    ALTER TABLE [cent_products] DROP CONSTRAINT [CK_cent_product_access];
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE TABLE [cent_recommended_bundles] (
        [Id] nvarchar(100) NOT NULL,
        [Title] nvarchar(256) NOT NULL,
        [SubjectsJson] nvarchar(max) NOT NULL,
        [Rationale] nvarchar(max) NOT NULL,
        [Evidence] nvarchar(max) NOT NULL,
        [Active] bit NOT NULL,
        CONSTRAINT [PK_cent_recommended_bundles] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE TABLE [cent_subject_access] (
        [Id] uniqueidentifier NOT NULL,
        [EntitlementId] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [SubjectId] nvarchar(24) NOT NULL,
        [ContentReleaseId] nvarchar(100) NOT NULL,
        [StartsAt] datetimeoffset NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [RevokedAt] datetimeoffset NULL,
        CONSTRAINT [PK_cent_subject_access] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_cent_subject_term] CHECK ([ExpiresAt] > [StartsAt]),
        CONSTRAINT [FK_cent_subject_access_cent_courses_SubjectId] FOREIGN KEY ([SubjectId]) REFERENCES [cent_courses] ([Code]),
        CONSTRAINT [FK_cent_subject_access_cent_entitlements_EntitlementId] FOREIGN KEY ([EntitlementId]) REFERENCES [cent_entitlements] ([Id]),
        CONSTRAINT [FK_cent_subject_access_cent_orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [cent_orders] ([Id]),
        CONSTRAINT [FK_cent_subject_access_cent_users_UserId] FOREIGN KEY ([UserId]) REFERENCES [cent_users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE TABLE [cent_subject_offers] (
        [SubjectId] nvarchar(24) NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [ContentReleaseId] nvarchar(100) NOT NULL,
        [ReviewedBy] nvarchar(256) NULL,
        [ReviewedAt] datetimeoffset NULL,
        [Available] bit NOT NULL,
        CONSTRAINT [PK_cent_subject_offers] PRIMARY KEY ([SubjectId]),
        CONSTRAINT [FK_cent_subject_offers_cent_courses_SubjectId] FOREIGN KEY ([SubjectId]) REFERENCES [cent_courses] ([Code]),
        CONSTRAINT [FK_cent_subject_offers_cent_products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [cent_products] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE TABLE [cent_subject_order_lines] (
        [OrderId] uniqueidentifier NOT NULL,
        [SubjectId] nvarchar(24) NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [ContentReleaseId] nvarchar(100) NOT NULL,
        [ListRial] bigint NOT NULL,
        [DiscountRial] bigint NOT NULL,
        [PayableRial] bigint NOT NULL,
        CONSTRAINT [PK_cent_subject_order_lines] PRIMARY KEY ([OrderId], [SubjectId]),
        CONSTRAINT [CK_cent_subject_line_amount] CHECK ([ListRial] > 0 AND [DiscountRial] >= 0 AND [PayableRial] = [ListRial] - [DiscountRial] AND [PayableRial] > 0),
        CONSTRAINT [FK_cent_subject_order_lines_cent_orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [cent_orders] ([Id]),
        CONSTRAINT [FK_cent_subject_order_lines_cent_products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [cent_products] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE TABLE [cent_subject_quotes] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [OrderId] uniqueidentifier NULL,
        CONSTRAINT [PK_cent_subject_quotes] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_cent_subject_quotes_cent_orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [cent_orders] ([Id]),
        CONSTRAINT [FK_cent_subject_quotes_cent_users_UserId] FOREIGN KEY ([UserId]) REFERENCES [cent_users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE TABLE [cent_subject_orders] (
        [OrderId] uniqueidentifier NOT NULL,
        [QuoteId] uniqueidentifier NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [InitiationState] nvarchar(24) NOT NULL,
        [DevelopmentOutcome] nvarchar(16) NULL,
        CONSTRAINT [PK_cent_subject_orders] PRIMARY KEY ([OrderId]),
        CONSTRAINT [FK_cent_subject_orders_cent_orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [cent_orders] ([Id]),
        CONSTRAINT [FK_cent_subject_orders_cent_subject_quotes_QuoteId] FOREIGN KEY ([QuoteId]) REFERENCES [cent_subject_quotes] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    EXEC(N'ALTER TABLE [cent_products] ADD CONSTRAINT [CK_cent_product_access] CHECK (([Kind] IN (''TRAINING'', ''SUBJECT'') AND [TrainingId] IS NOT NULL AND [DurationDays] IS NULL) OR ([Kind] = ''SUBSCRIPTION'' AND [TrainingId] IS NULL AND [DurationDays] BETWEEN 1 AND 366))');
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE INDEX [IX_cent_subject_access_EntitlementId] ON [cent_subject_access] ([EntitlementId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_subject_access_OrderId_SubjectId] ON [cent_subject_access] ([OrderId], [SubjectId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE INDEX [IX_cent_subject_access_SubjectId] ON [cent_subject_access] ([SubjectId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE INDEX [IX_cent_subject_access_UserId_SubjectId_ExpiresAt] ON [cent_subject_access] ([UserId], [SubjectId], [ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_subject_offers_ProductId] ON [cent_subject_offers] ([ProductId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE INDEX [IX_cent_subject_order_lines_ProductId] ON [cent_subject_order_lines] ([ProductId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE UNIQUE INDEX [IX_cent_subject_orders_QuoteId] ON [cent_subject_orders] ([QuoteId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE INDEX [IX_cent_subject_quotes_OrderId] ON [cent_subject_quotes] ([OrderId]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    CREATE INDEX [IX_cent_subject_quotes_UserId_ExpiresAt] ON [cent_subject_quotes] ([UserId], [ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261122102343_SubjectCommerce'
)
BEGIN
    INSERT INTO [cent_migrations_history] ([MigrationId], [ProductVersion])
    VALUES (N'20261122102343_SubjectCommerce', N'10.0.11');
END;

COMMIT;
GO

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

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261124155704_ReaderPilotAnchors'
)
BEGIN
    ALTER TABLE [cent_notes] ADD [AnchorJson] nvarchar(2000) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261124155704_ReaderPilotAnchors'
)
BEGIN
    INSERT INTO [cent_migrations_history] ([MigrationId], [ProductVersion])
    VALUES (N'20261124155704_ReaderPilotAnchors', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261124174931_ReaderProtectionBudgets'
)
BEGIN
    CREATE TABLE [cent_reader_budgets] (
        [Key] nvarchar(96) NOT NULL,
        [StateJson] nvarchar(max) NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_cent_reader_budgets] PRIMARY KEY ([Key])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261124174931_ReaderProtectionBudgets'
)
BEGIN
    CREATE INDEX [IX_cent_reader_budgets_UpdatedAt] ON [cent_reader_budgets] ([UpdatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [cent_migrations_history]
    WHERE [MigrationId] = N'20261124174931_ReaderProtectionBudgets'
)
BEGIN
    INSERT INTO [cent_migrations_history] ([MigrationId], [ProductVersion])
    VALUES (N'20261124174931_ReaderProtectionBudgets', N'10.0.11');
END;

COMMIT;
GO

SELECT [MigrationId], [ProductVersion]
FROM [dbo].[cent_migrations_history]
ORDER BY [MigrationId];

