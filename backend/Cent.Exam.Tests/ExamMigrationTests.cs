using Cent.Module;
using Cent.Module.Exams;
using Cent.Module.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Cent.Exam.Tests;

public sealed class ExamMigrationTests
{
    [Fact]
    public void Migration_only_adds_six_Cent_tables_indexes_and_two_disabled_blueprints()
    {
        var operations = new VersionedMockExams().UpOperations;
        var tables = operations.OfType<CreateTableOperation>().ToArray();
        Assert.Equal(6, tables.Length);
        Assert.All(tables, t => {
            Assert.StartsWith("cent_", t.Name);
            Assert.All(t.ForeignKeys, fk => { Assert.StartsWith("cent_", fk.PrincipalTable); Assert.Equal(ReferentialAction.NoAction, fk.OnDelete); });
        });
        Assert.All(operations, op => Assert.True(op is CreateTableOperation or CreateIndexOperation or InsertDataOperation));
        var insert = Assert.Single(operations.OfType<InsertDataOperation>());
        Assert.Equal("cent_exam_versions", insert.Table); Assert.Equal(2, insert.Values.GetLength(0));
        var enabled = Array.IndexOf(insert.Columns, "AvailableForNewStarts");
        Assert.False((bool)insert.Values[0, enabled]!); Assert.False((bool)insert.Values[1, enabled]!);
        Assert.Contains(operations.OfType<CreateIndexOperation>(), i => i.IsUnique && i.Filter == "[State] = 'ACTIVE'");
    }
    [Fact]
    public void Offline_SQL_generation_and_model_match_need_no_connection()
    {
        using var db = new CentDbContext(new DbContextOptionsBuilder<CentDbContext>()
            .UseSqlServer("Server=offline.invalid;Database=NeverConnect;Integrated Security=true;TrustServerCertificate=true",
                sql => sql.MigrationsHistoryTable("cent_migrations_history")).Options);
        Assert.False(db.GetService<IMigrator>().HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript("20261109184203_AddCentGoogleAuthentication",
            "20261120134304_VersionedMockExams", MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE [cent_mock_attempts]", sql);
        Assert.Contains("[cent_migrations_history]", sql);
        Assert.DoesNotContain("DROP TABLE", sql); Assert.DoesNotContain("ALTER TABLE", sql);
        Assert.DoesNotContain("UPDATE [cent_attempts]", sql); Assert.DoesNotContain("DELETE", sql);
        Assert.DoesNotContain("Flashcards", sql); Assert.DoesNotContain("Lerona", sql);
    }
    [Fact]
    public void Frozen_intent_recovers_once_and_does_not_reopen_answers()
    {
        var f = new ExamFixture(); var run = f.Run();
        foreach (var section in run.Sections) {
            section.State = "CLOSED_TIMEOUT"; section.ActivatedAt = f.Clock.Now;
            section.Deadline = f.Clock.Now.AddSeconds(section.DurationSeconds); section.ClosedAt = section.Deadline;
        }
        run.Mock.Status = "SUBMITTING"; run.Mock.ActiveSectionId = null; run.Mock.ActiveDeadline = null;
        run.Mock.FinalizationReason = "final_section_timeout"; run.Mock.EffectiveEndedAt = f.Clock.Now.AddMinutes(140);
        CentExamEngine.Reconcile(run, f.Clock.Now.AddMinutes(141));
        var saved = run.Mock.ResultJson; Assert.Equal("EXPIRED", run.Mock.Status);
        CentExamEngine.Finalize(run); Assert.Equal(saved, run.Mock.ResultJson);
        Assert.Equal(f.Clock.Now.AddMinutes(140), run.Mock.EffectiveEndedAt);
    }
    [Fact]
    public void Server_clock_rollback_rejects_writes_without_extending_deadlines()
    {
        var f = new ExamFixture(); var run = f.Run(); var deadline = run.Mock.ActiveDeadline;
        var command = new ExamCommand(Guid.NewGuid(), run.Sections[0].Id, 1, run.Questions[0].QuestionId, "b");
        var ex = Assert.Throws<CentException>(() => CentExamEngine.Apply(run, "answer", command, f.Clock.Now.AddMinutes(-1)));
        Assert.Equal(503, ex.Status); Assert.Equal(deadline, run.Mock.ActiveDeadline);
        Assert.Null(run.Questions[0].Answer); Assert.Empty(run.Receipts);
    }
    [Fact]
    public void SQL_Server_lock_queries_and_history_cursor_translate_offline()
    {
        using var db = new CentDbContext(new DbContextOptionsBuilder<CentDbContext>()
            .UseSqlServer("Server=offline.invalid;Database=NeverConnect;Integrated Security=true;TrustServerCertificate=true").Options);
        var id = Guid.NewGuid(); var owner = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        var locked = db.MockAttempts.FromSqlInterpolated($"SELECT m.* FROM cent_mock_attempts m WITH (UPDLOCK, ROWLOCK) INNER JOIN cent_attempts a ON a.Id = m.AttemptId WHERE m.AttemptId = {id} AND a.UserId = {owner}").Take(2).ToQueryString();
        Assert.Contains("UPDLOCK", locked); Assert.Contains("cent_attempts", locked);
        var history = db.Attempts.Where(a => a.UserId == owner && (a.CreatedAt < at || (a.CreatedAt == at && a.Id.CompareTo(id) < 0)))
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(20).ToQueryString();
        Assert.Contains("ORDER BY", history); Assert.Contains("cent_attempts", history);
    }
    [Fact]
    public void Curriculum_migration_adds_only_two_Cent_companions_and_never_updates_old_data()
    {
        var migration = new VersionedCurriculum();
        Assert.All(migration.UpOperations, op => Assert.True(op is CreateTableOperation or CreateIndexOperation));
        Assert.Equal(new[] { "cent_curriculum_revisions", "cent_practice_content" }, migration.UpOperations.OfType<CreateTableOperation>().Select(t => t.Name));
        Assert.All(migration.UpOperations.OfType<CreateTableOperation>().SelectMany(t => t.ForeignKeys), fk => {
            Assert.StartsWith("cent_", fk.PrincipalTable); Assert.Equal(ReferentialAction.NoAction, fk.OnDelete);
        });
        using var db = new CentDbContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlServer(
            "Server=offline.invalid;Database=NeverConnect;Integrated Security=true;TrustServerCertificate=true",
            sql => sql.MigrationsHistoryTable("cent_migrations_history")).Options);
        var sql = db.GetService<IMigrator>().GenerateScript("20261120134304_VersionedMockExams", "20261122081723_VersionedCurriculum", MigrationsSqlGenerationOptions.Idempotent);
        Assert.DoesNotContain("ALTER TABLE", sql); Assert.DoesNotContain("DROP TABLE", sql);
        Assert.DoesNotContain("UPDATE ", sql); Assert.DoesNotContain("DELETE ", sql);
        Assert.Contains("[cent_migrations_history]", sql);
    }
}
