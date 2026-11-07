using Microsoft.EntityFrameworkCore;

namespace Cent.Module;

public sealed class CentCourse
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string EnglishTitle { get; set; } = "";
    public int SortOrder { get; set; }
    public Guid? TrainingId { get; set; }
    public bool Active { get; set; } = true;
}
public sealed class CentChapter
{
    public Guid Id { get; set; }
    public string ImportKey { get; set; } = "";
    public string CourseCode { get; set; } = "";
    public Guid SourceId { get; set; }
    public string Title { get; set; } = "";
    public int SortOrder { get; set; }
}
public sealed class CentTopic
{
    public Guid Id { get; set; }
    public string ImportKey { get; set; } = "";
    public Guid ChapterId { get; set; }
    public string CourseCode { get; set; } = "";
    public string Title { get; set; } = "";
    public string EnglishTitle { get; set; } = "";
    public int SortOrder { get; set; }
    public bool Foundation { get; set; }
}
public sealed class CentSource
{
    public Guid Id { get; set; }
    public string ImportKey { get; set; } = "";
    public string CourseCode { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Title { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string OriginalHtml { get; set; } = "";
    public string PlainText { get; set; } = "";
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class CentSourceSection
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public Guid? TopicId { get; set; }
    public string ImportKey { get; set; } = "";
    public string Heading { get; set; } = "";
    public int SortOrder { get; set; }
    public string Text { get; set; } = "";
    public string Html { get; set; } = "";
}
public sealed class CentSourceExercise
{
    public Guid Id { get; set; }
    public Guid SourceSectionId { get; set; }
    public Guid? TopicId { get; set; }
    public string ImportKey { get; set; } = "";
    public string Text { get; set; } = "";
    public string OptionsJson { get; set; } = "[]";
    public int? OriginalCorrectAnswer { get; set; }
    public string Solution { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ReviewNote { get; set; }
}
public sealed class CentCurriculumImport
{
    public string PackageHash { get; set; } = "";
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public int SourceCount { get; set; }
    public int TopicCount { get; set; }
    public int QuestionCount { get; set; }
}

public static class CentCurriculumModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<CentCourse>(e => {
            e.ToTable("cent_courses"); e.HasKey(x => x.Code); e.Property(x => x.Code).HasMaxLength(24);
            e.Property(x => x.Title).HasMaxLength(256); e.Property(x => x.EnglishTitle).HasMaxLength(256);
            e.HasOne<CentTraining>().WithMany().HasForeignKey(x => x.TrainingId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentSource>(e => {
            e.ToTable("cent_sources"); e.HasKey(x => x.Id); e.Property(x => x.ImportKey).HasMaxLength(300); e.HasIndex(x => x.ImportKey).IsUnique();
            e.Property(x => x.CourseCode).HasMaxLength(24); e.Property(x => x.FileName).HasMaxLength(300);
            e.Property(x => x.Title).HasMaxLength(500); e.Property(x => x.OriginalTitle).HasMaxLength(500); e.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false);
            e.HasOne<CentCourse>().WithMany().HasForeignKey(x => x.CourseCode).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentChapter>(e => {
            e.ToTable("cent_chapters"); e.HasKey(x => x.Id); e.Property(x => x.ImportKey).HasMaxLength(300); e.HasIndex(x => x.ImportKey).IsUnique();
            e.Property(x => x.CourseCode).HasMaxLength(24); e.Property(x => x.Title).HasMaxLength(500);
            e.HasOne<CentCourse>().WithMany().HasForeignKey(x => x.CourseCode).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentTopic>(e => {
            e.ToTable("cent_topics"); e.HasKey(x => x.Id); e.Property(x => x.ImportKey).HasMaxLength(300); e.HasIndex(x => x.ImportKey).IsUnique();
            e.Property(x => x.CourseCode).HasMaxLength(24); e.Property(x => x.Title).HasMaxLength(500); e.Property(x => x.EnglishTitle).HasMaxLength(256);
            e.HasOne<CentChapter>().WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentCourse>().WithMany().HasForeignKey(x => x.CourseCode).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.CourseCode, x.ChapterId, x.SortOrder });
        });
        b.Entity<CentSourceSection>(e => {
            e.ToTable("cent_source_sections"); e.HasKey(x => x.Id); e.Property(x => x.ImportKey).HasMaxLength(300); e.HasIndex(x => x.ImportKey).IsUnique();
            e.Property(x => x.Heading).HasMaxLength(500); e.HasIndex(x => new { x.SourceId, x.SortOrder }).IsUnique();
            e.HasOne<CentSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentTopic>().WithMany().HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentSourceExercise>(e => {
            e.ToTable("cent_source_exercises"); e.HasKey(x => x.Id); e.Property(x => x.ImportKey).HasMaxLength(300); e.HasIndex(x => x.ImportKey).IsUnique();
            e.Property(x => x.Status).HasMaxLength(32); e.Property(x => x.ReviewNote).HasMaxLength(4000);
            e.HasOne<CentSourceSection>().WithMany().HasForeignKey(x => x.SourceSectionId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentTopic>().WithMany().HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentQuestion>(e => {
            e.Property(x => x.ImportKey).HasMaxLength(300); e.HasIndex(x => x.ImportKey).IsUnique().HasFilter("[ImportKey] IS NOT NULL");
            e.Property(x => x.Origin).HasMaxLength(32).HasDefaultValue("manual");
            e.HasOne<CentTopic>().WithMany().HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentSourceSection>().WithMany().HasForeignKey(x => x.SourceSectionId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentSourceExercise>().WithMany().HasForeignKey(x => x.SourceExerciseId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.Subject, x.Active, x.TopicId });
        });
        b.Entity<CentDocument>().HasOne<CentSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<CentAttempt>(e => {
            e.Property(x => x.CourseCodesJson).HasDefaultValue("[]");
            e.Property(x => x.AssessmentMode).HasDefaultValue("overview");
            e.Property(x => x.DurationMinutes).HasDefaultValue(60);
        });
        b.Entity<CentCurriculumImport>(e => {
            e.ToTable("cent_curriculum_imports"); e.HasKey(x => x.PackageHash); e.Property(x => x.PackageHash).HasMaxLength(64).IsUnicode(false);
        });
    }
}
