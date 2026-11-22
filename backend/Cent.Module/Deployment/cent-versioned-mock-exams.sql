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

