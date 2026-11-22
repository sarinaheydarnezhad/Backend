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

