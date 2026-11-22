using Microsoft.EntityFrameworkCore;

namespace Cent.Module.Exams;

public sealed class CentExamVersion
{
    public string Id { get; set; } = "";
    public string ConfigurationRevision { get; set; } = "";
    public string DefinitionJson { get; set; } = "";
    public string Status { get; set; } = "DRAFT";
    public bool AvailableForNewStarts { get; set; }
    public string? SyllabusRevisionId { get; set; }
    public string? ContentReleaseId { get; set; }
    public int Revision { get; set; } = 1;
}
public sealed class CentSyllabusRevision
{
    public string Id { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public DateTimeOffset RetrievedAt { get; set; }
    public string EvidenceJson { get; set; } = "{}";
}
// A reviewed immutable manifest over existing CentQuestion identities; not another CMS.
public sealed class CentExamContentRelease
{
    public string Id { get; set; } = "";
    public string ExamVersionId { get; set; } = "";
    public string SyllabusRevisionId { get; set; } = "";
    public string ManifestJson { get; set; } = "";
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
}
public sealed class CentMockAttempt
{
    public Guid AttemptId { get; set; }
    public string ExamVersionId { get; set; } = "";
    public string ConfigurationRevision { get; set; } = "";
    public string RulesSnapshotJson { get; set; } = "";
    public string Status { get; set; } = "ACTIVE";
    public int Revision { get; set; } = 1;
    public Guid? ActiveSectionId { get; set; }
    public DateTimeOffset? ActiveDeadline { get; set; }
    public DateTimeOffset? EffectiveEndedAt { get; set; }
    public string? FinalizationReason { get; set; }
    public string? ResultJson { get; set; }
}
public sealed class CentAttemptSection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AttemptId { get; set; }
    public string SectionKey { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public int Order { get; set; }
    public int QuestionCount { get; set; }
    public int DurationSeconds { get; set; }
    public string State { get; set; } = "PENDING";
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
public sealed class CentExamCommandReceipt
{
    public Guid AttemptId { get; set; }
    public Guid CommandId { get; set; }
    public string PayloadHash { get; set; } = "";
    public string ReceiptJson { get; set; } = "";
}

public static class CentExamModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<CentSyllabusRevision>(e => {
            e.ToTable("cent_syllabus_revisions"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100); e.Property(x => x.SourceUrl).HasMaxLength(2000);
        });
        b.Entity<CentExamContentRelease>(e => {
            e.ToTable("cent_exam_content_releases"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100); e.Property(x => x.ExamVersionId).HasMaxLength(100);
            e.Property(x => x.SyllabusRevisionId).HasMaxLength(100);
            e.HasOne<CentSyllabusRevision>().WithMany().HasForeignKey(x => x.SyllabusRevisionId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentUser>().WithMany().HasForeignKey(x => x.ReviewedBy).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentExamVersion>(e => {
            e.ToTable("cent_exam_versions"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100); e.Property(x => x.ConfigurationRevision).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(24); e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.SyllabusRevisionId).HasMaxLength(100); e.Property(x => x.ContentReleaseId).HasMaxLength(100);
            e.HasIndex(x => x.ConfigurationRevision).IsUnique();
            e.HasOne<CentSyllabusRevision>().WithMany().HasForeignKey(x => x.SyllabusRevisionId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentExamContentRelease>().WithMany().HasForeignKey(x => x.ContentReleaseId).OnDelete(DeleteBehavior.NoAction);
            foreach (var d in new[] { CentExamBlueprints.Historical, CentExamBlueprints.Current })
                e.HasData(new CentExamVersion { Id = d.ExamVersionId, ConfigurationRevision = d.ConfigurationRevision,
                    DefinitionJson = CentExamJson.Write(d), Status = "PUBLISHED" });
        });
        b.Entity<CentMockAttempt>(e => {
            e.ToTable("cent_mock_attempts"); e.HasKey(x => x.AttemptId);
            e.Property(x => x.ExamVersionId).HasMaxLength(100); e.Property(x => x.ConfigurationRevision).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(24); e.Property(x => x.FinalizationReason).HasMaxLength(40);
            e.Property(x => x.Revision).IsConcurrencyToken(); e.HasIndex(x => new { x.Status, x.ActiveDeadline });
            e.HasOne<CentAttempt>().WithOne().HasForeignKey<CentMockAttempt>(x => x.AttemptId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentExamVersion>().WithMany().HasForeignKey(x => x.ExamVersionId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentAttemptSection>(e => {
            e.ToTable("cent_attempt_sections", t => {
                t.HasCheckConstraint("CK_cent_attempt_section_quota", "[QuestionCount] > 0 AND [DurationSeconds] > 0");
                t.HasCheckConstraint("CK_cent_attempt_section_deadline", "([ActivatedAt] IS NULL AND [Deadline] IS NULL AND [State] = 'PENDING') OR ([ActivatedAt] IS NOT NULL AND [Deadline] IS NOT NULL)");
            });
            e.HasKey(x => x.Id); e.Property(x => x.SubjectId).HasMaxLength(24);
            e.Property(x => x.SectionKey).HasMaxLength(24); e.Property(x => x.State).HasMaxLength(24);
            e.HasIndex(x => new { x.AttemptId, x.Order }).IsUnique();
            e.HasIndex(x => new { x.AttemptId, x.SectionKey }).IsUnique();
            e.HasIndex(x => x.AttemptId).IsUnique().HasDatabaseName("IX_cent_attempt_sections_active")
                .HasFilter("[State] = 'ACTIVE'");
            e.HasOne<CentMockAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentExamCommandReceipt>(e => {
            e.ToTable("cent_exam_command_receipts"); e.HasKey(x => new { x.AttemptId, x.CommandId });
            e.Property(x => x.PayloadHash).HasMaxLength(64).IsUnicode(false);
            e.HasOne<CentMockAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.NoAction);
        });
    }
}
