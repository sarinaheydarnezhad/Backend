using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cent.Module;

public class CentDbContext(DbContextOptions<CentDbContext> options) : DbContext(options)
{
    public DbSet<CentUser> Users => Set<CentUser>();
    public DbSet<CentSession> Sessions => Set<CentSession>();
    public DbSet<CentTraining> Trainings => Set<CentTraining>();
    public DbSet<CentDocument> Documents => Set<CentDocument>();
    public DbSet<CentDocumentPage> Pages => Set<CentDocumentPage>();
    public DbSet<CentSection> Sections => Set<CentSection>();
    public DbSet<CentNote> Notes => Set<CentNote>();
    public DbSet<CentProgress> Progress => Set<CentProgress>();
    public DbSet<CentProduct> Products => Set<CentProduct>();
    public DbSet<CentOrder> Orders => Set<CentOrder>();
    public DbSet<CentEntitlement> Entitlements => Set<CentEntitlement>();
    public DbSet<CentPaymentEvent> PaymentEvents => Set<CentPaymentEvent>();
    public DbSet<CentQuestion> Questions => Set<CentQuestion>();
    public DbSet<CentAttempt> Attempts => Set<CentAttempt>();
    public DbSet<CentAttemptQuestion> AttemptQuestions => Set<CentAttemptQuestion>();
    public DbSet<CentAiAnalysis> Analyses => Set<CentAiAnalysis>();
    public DbSet<CentAuditEvent> Audit => Set<CentAuditEvent>();
    public DbSet<CentCourse> Courses => Set<CentCourse>();
    public DbSet<CentChapter> Chapters => Set<CentChapter>();
    public DbSet<CentTopic> Topics => Set<CentTopic>();
    public DbSet<CentSource> Sources => Set<CentSource>();
    public DbSet<CentSourceSection> SourceSections => Set<CentSourceSection>();
    public DbSet<CentSourceExercise> SourceExercises => Set<CentSourceExercise>();
    public DbSet<CentCurriculumImport> CurriculumImports => Set<CentCurriculumImport>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        CentCurriculumModel.Configure(b);
        b.Entity<CentUser>(e => {
            e.ToTable("cent_users"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120); e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.NormalizedEmail).HasMaxLength(256); e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.Role).HasMaxLength(16); e.HasIndex(x => x.NormalizedEmail).IsUnique();
        });
        b.Entity<CentSession>(e => {
            e.ToTable("cent_sessions"); e.HasKey(x => x.Id); e.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false);
            e.HasIndex(x => x.TokenHash).IsUnique(); e.HasIndex(x => new { x.UserId, x.ExpiresAt });
            e.HasOne<CentUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentTraining>(e => {
            e.ToTable("cent_trainings"); e.HasKey(x => x.Id); e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(8000);
        });
        b.Entity<CentDocument>(e => {
            e.ToTable("cent_documents"); e.HasKey(x => x.Id); e.Property(x => x.Title).HasMaxLength(200);
            e.HasOne<CentTraining>().WithMany().HasForeignKey(x => x.TrainingId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.TrainingId, x.SortOrder });
        });
        b.Entity<CentDocumentPage>(e => {
            e.ToTable("cent_document_pages"); e.HasKey(x => new { x.DocumentId, x.Number });
            e.HasOne<CentDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentSection>(e => {
            e.ToTable("cent_document_sections"); e.HasKey(x => x.Id); e.Property(x => x.Title).HasMaxLength(200);
            e.HasOne<CentDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.DocumentId, x.StartPage });
        });
        b.Entity<CentNote>(e => {
            e.ToTable("cent_notes"); e.HasKey(x => x.Id); e.Property(x => x.Body).HasMaxLength(4000);
            e.Property(x => x.Revision).IsConcurrencyToken(); e.HasIndex(x => new { x.UserId, x.DocumentId, x.Page });
            e.HasOne<CentUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentProgress>(e => {
            e.ToTable("cent_study_progress"); e.HasKey(x => new { x.UserId, x.DocumentId });
            e.HasOne<CentUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentProduct>(e => {
            e.ToTable("cent_products", t => {
                t.HasCheckConstraint("CK_cent_product_access", "([Kind] = 'TRAINING' AND [TrainingId] IS NOT NULL AND [DurationDays] IS NULL) OR ([Kind] = 'SUBSCRIPTION' AND [TrainingId] IS NULL AND [DurationDays] BETWEEN 1 AND 366)");
                t.HasCheckConstraint("CK_cent_product_price", "[PriceRial] > 0");
            });
            e.HasKey(x => x.Id); e.Property(x => x.Title).HasMaxLength(200); e.Property(x => x.Kind).HasMaxLength(16);
            e.HasOne<CentTraining>().WithMany().HasForeignKey(x => x.TrainingId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentOrder>(e => {
            e.ToTable("cent_orders"); e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200); e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(24); e.Property(x => x.Provider).HasMaxLength(16);
            e.Property(x => x.Authority).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.Reference).HasMaxLength(100).IsUnicode(false);
            e.HasIndex(x => new { x.UserId, x.RequestId }).IsUnique();
            e.HasIndex(x => x.Authority).IsUnique().HasFilter("[Authority] IS NOT NULL");
            e.HasIndex(x => new { x.Provider, x.Reference }).IsUnique().HasFilter("[Reference] IS NOT NULL");
            e.HasOne<CentUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentTraining>().WithMany().HasForeignKey(x => x.TrainingId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentPaymentEvent>(e => {
            e.ToTable("cent_payment_events"); e.HasKey(x => x.Id); e.Property(x => x.Action).HasMaxLength(24);
            e.HasOne<CentOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.OrderId, x.CreatedAt });
        });
        b.Entity<CentEntitlement>(e => {
            e.ToTable("cent_entitlements"); e.HasKey(x => x.Id); e.Property(x => x.Kind).HasMaxLength(16);
            e.HasIndex(x => x.OrderId).IsUnique(); e.HasIndex(x => new { x.UserId, x.Kind, x.TrainingId });
            e.HasOne<CentUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentTraining>().WithMany().HasForeignKey(x => x.TrainingId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentQuestion>(e => {
            e.ToTable("cent_questions"); e.HasKey(x => x.Id); e.Property(x => x.Subject).HasMaxLength(24);
            e.Property(x => x.Topic).HasMaxLength(500); e.Property(x => x.Difficulty).HasMaxLength(16);
            e.Property(x => x.Kind).HasMaxLength(16); e.Property(x => x.Text).HasMaxLength(8000);
            e.Property(x => x.OptionsJson).HasMaxLength(8000); e.Property(x => x.Explanation).HasMaxLength(8000);
            e.HasIndex(x => new { x.Active, x.Kind });
        });
        b.Entity<CentAttempt>(e => {
            e.ToTable("cent_attempts"); e.HasKey(x => x.Id); e.Property(x => x.Kind).HasMaxLength(16); e.Property(x => x.AssessmentMode).HasMaxLength(36);
            e.HasIndex(x => new { x.UserId, x.RequestId }).IsUnique();
            e.HasOne<CentUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentAttemptQuestion>(e => {
            e.ToTable("cent_attempt_questions"); e.HasKey(x => new { x.AttemptId, x.Position });
            e.HasOne<CentAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentAiAnalysis>(e => {
            e.ToTable("cent_ai_analyses"); e.HasKey(x => x.Id); e.Property(x => x.Provider).HasMaxLength(24);
            e.HasIndex(x => x.AttemptId).IsUnique();
            e.HasOne<CentAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentAuditEvent>(e => {
            e.ToTable("cent_audit_events"); e.HasKey(x => x.Id); e.Property(x => x.Action).HasMaxLength(64);
            e.HasIndex(x => x.CreatedAt);
        });
    }
}

public sealed class CentDbContextFactory : IDesignTimeDbContextFactory<CentDbContext>
{
    public CentDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__CentDb")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__FlashcardsDb")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=CentDev;Trusted_Connection=True;TrustServerCertificate=True";
        return new CentDbContext(new DbContextOptionsBuilder<CentDbContext>()
            .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("cent_migrations_history")).Options);
    }
}
