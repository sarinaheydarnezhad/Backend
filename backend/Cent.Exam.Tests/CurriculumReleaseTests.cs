using System.Security.Cryptography;
using System.Text.Json;
using Cent.Module;
using Cent.Module.Curriculum;
using Cent.Module.Exams;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cent.Exam.Tests;

public sealed class CurriculumReleaseTests
{
    private static Corpus Package() => CentCurriculumImporter.Bundled();
    [Fact]
    public void All_six_subjects_have_mapped_Persian_lessons_and_English_reviewed_assessments()
    {
        var p = Package(); CentCurriculumImporter.Validate(p);
        Assert.Equal(6, CurriculumIdentity.Courses.Length);
        Assert.Equal(31, p.Release!.Nodes.Length);
        Assert.Equal(p.Topics.Count, p.Release.Topics.Length);
        foreach (var node in p.Release.Nodes)
            Assert.Contains(p.Release.Topics, t => t.Status == "valid" && t.NodeKeys.Contains(node.Key));
        var current = p.Questions.Where(q => q.Origin == "authored-2026-reviewed").ToArray();
        Assert.NotEmpty(current);
        Assert.All(current, q => {
            Assert.Equal(5, q.Options.Length); Assert.InRange(q.CorrectAnswer, 0, 4);
            Assert.False(CurriculumReleases.ContainsPersian(q.Text + string.Join(" ", q.Options) + q.Explanation));
            Assert.Contains(p.Release.Reviews, r => r.Key == q.Key && r.Status == "academically-reviewed" && r.Reviewer.Contains("Codex"));
        });
        Assert.All(p.Release.Resources.Where(r => r.Kind == "lesson"), r => {
            Assert.Equal("fa", r.Language);
            Assert.True(CurriculumReleases.ContainsPersian(p.Sections.Single(s => s.Key == r.Key).Text));
        });
    }
    [Fact]
    public void Original_bank_sources_and_question_ids_are_retained_and_not_automatically_certified()
    {
        var legacy = CentCurriculumImporter.LegacyBundled(); var p = Package();
        Assert.Equal(819, legacy.Questions.Count); Assert.Equal(666, legacy.Exercises.Count);
        foreach (var q in legacy.Questions) {
            Assert.Equal(JsonSerializer.Serialize(q), JsonSerializer.Serialize(p.Questions.Single(x => x.Key == q.Key)));
            Assert.Equal("requires-source-verification", p.Release!.Reviews.Single(r => r.Key == q.Key).Status);
        }
        Assert.Equal(legacy.Sources, p.Sources.Take(legacy.Sources.Count));
        Assert.Equal(legacy.Topics, p.Topics.Take(legacy.Topics.Count));
        Assert.All(legacy.Questions, q => Assert.Equal(4, q.Options.Length));
    }
    [Fact]
    public void All_embedded_source_checksums_match_original_and_new_bytes()
    {
        var assembly = typeof(CentCurriculumImporter).Assembly;
        foreach (var source in Package().Sources) {
            using var stream = assembly.GetManifestResourceStream("Cent.Module.Content.Sources." + source.File.Replace('/', '.'));
            Assert.NotNull(stream);
            Assert.Equal(source.Sha256, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Released_candidates_assemble_exact_quotas_and_freeze_atomic_passage_groups(bool historical)
    {
        var p = Package(); var blueprint = historical ? CentExamBlueprints.Historical : CentExamBlueprints.Current;
        var d = blueprint with { SyllabusRevisionId = p.Release!.SyllabusRevisionId, ContentReleaseId = p.Release.Id, SubmissionPolicyId = "expired-feedback-v1" };
        var manifest = CurriculumReleases.Manifest(p, d.ExamVersionId);
        for (var i = 0; i < 10; i++) {
            var run = CentExamAssembly.Assemble(Guid.NewGuid(), Guid.NewGuid(), d, manifest, "test-only", DateTimeOffset.UtcNow);
            Assert.Equal(d.TotalQuestions, run.Questions.Count);
            Assert.Equal(run.Questions.Count, run.Questions.Select(q => q.QuestionId).Distinct().Count());
            foreach (var s in run.Sections) Assert.Equal(s.QuestionCount,
                run.Questions.Count(q => CentExamJson.Read<AssignedExamQuestion>(q.SnapshotJson).SectionId == s.Id));
            Assert.All(run.Rules.Groups, g => {
                Assert.Single(run.Rules.Passages, x => x.PassageRevisionId == g.PassageRevisionId);
                Assert.Equal(g.QuestionRevisionIds.Length, run.Questions.Count(q => CentExamJson.Read<AssignedExamQuestion>(q.SnapshotJson).Question.GroupRevisionId == g.GroupRevisionId));
            });
            Assert.All(run.Rules.Passages, passage => Assert.Equal(p.Release.Passages.Single(x => x.Id == passage.PassageRevisionId).Text, passage.Text));
            var active = JsonSerializer.Serialize(CentExamService.ActiveContent(run, DateTimeOffset.UtcNow), CentExamJson.Options);
            Assert.DoesNotContain("correctOptionId", active); Assert.DoesNotContain("explanation", active);
        }
    }
    [Fact]
    public void Orphan_passages_cross_subject_mappings_and_missing_review_evidence_are_rejected()
    {
        var p = Package(); var r = p.Release!;
        p.Release = r with { Passages = [] };
        Assert.Throws<InvalidOperationException>(() => CentCurriculumImporter.Validate(p));
        p.Release = r with { Topics = r.Topics.Select((t,i) => i == 0 ? t with { NodeKeys = ["S01"] } : t).ToArray() };
        Assert.Throws<InvalidOperationException>(() => CentCurriculumImporter.Validate(p));
        p.Release = r with { Reviews = r.Reviews.Select(x => x.Status == "academically-reviewed" ? x with { Evidence = "" } : x).ToArray() };
        Assert.Throws<InvalidOperationException>(() => CentCurriculumImporter.Validate(p));
    }
    [Fact]
    public void Practice_never_splits_groups_or_orphans_dependents_under_limits_and_topic_filters()
    {
        var p = Package(); var r = p.Release!;
        var grouped = r.Groups.First();
        var ordered = grouped.QuestionKeys.Select(k => new CentQuestion { Id = CurriculumIdentity.Id("question:" + k) }).ToArray();
        Assert.Empty(CurriculumReleases.SelectPractice(ordered, r, ordered.Length - 1).Questions);
        Assert.Empty(CurriculumReleases.SelectPractice(ordered[..^1], r).Questions);
        var selected = CurriculumReleases.SelectPractice(ordered, r);
        Assert.Equal(ordered.Length, selected.Questions.Length); Assert.Single(selected.Passages);
        Assert.All(selected.Questions, q => Assert.Equal(grouped.PassageId, selected.PassageIds[q.Id]));
    }
    [Fact]
    public async Task Import_is_additive_idempotent_and_preserves_existing_publication_notes_and_results()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        using var db = new ReleaseTestContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(); // Disposable fixture only; never apply migrations.
        var importer = new CentCurriculumImporter(db);
        var first = await importer.Import();
        Assert.False(first.AlreadyImported);
        Assert.Equal(Package().Questions.Count, await db.Questions.CountAsync());
        Assert.Equal(6, await db.Courses.CountAsync());
        var reading = await db.Trainings.FindAsync(CurriculumIdentity.Id("training:reading"));
        Assert.False(reading!.IncludedInSubscription);
        var user = new CentUser { Email = "fixture@example.invalid", NormalizedEmail = "FIXTURE@EXAMPLE.INVALID" };
        db.Users.Add(user);
        var attempt = new CentAttempt { UserId = user.Id, RequestId = Guid.NewGuid(), SubmittedAt = DateTimeOffset.UtcNow, ResultJson = "{\"original\":true}" };
        db.Attempts.Add(attempt);
        var doc = await db.Documents.FirstAsync(); doc.Published = false;
        var note = new CentNote { UserId = user.Id, DocumentId = doc.Id, Page = 1, Body = "fixture note", Revision = 7 };
        db.Notes.Add(note); await db.SaveChangesAsync();
        var importRow = await db.CurriculumImports.SingleAsync(); db.CurriculumImports.Remove(importRow); await db.SaveChangesAsync();
        Assert.False((await importer.Import()).AlreadyImported); // Full replay, not just receipt fast path.
        Assert.True((await importer.Import()).AlreadyImported);
        db.ChangeTracker.Clear();
        Assert.False((await db.Documents.FindAsync(doc.Id))!.Published);
        Assert.Equal(7, (await db.Notes.FindAsync(note.Id))!.Revision);
        Assert.Equal("{\"original\":true}", (await db.Attempts.FindAsync(attempt.Id))!.ResultJson);
        var manifest = await db.ExamContentReleases.SingleAsync();
        Assert.Null(manifest.ReviewedAt); Assert.Null(manifest.ReviewedBy);
        Assert.All(await db.ExamVersions.ToArrayAsync(), v => Assert.False(v.AvailableForNewStarts));
    }
    [Fact]
    public void Import_preflight_rejects_conflicting_published_payloads_and_duplicate_keys()
    {
        Assert.Throws<InvalidOperationException>(() => CentCurriculumImporter.RequireSame("old", "replacement", "published"));
        var p = Package(); p.Questions.Add(p.Questions[0]);
        Assert.Throws<InvalidOperationException>(() => CentCurriculumImporter.Validate(p));
    }
    [Fact]
    public async Task Versioned_Practice_keeps_context_on_retry_and_legacy_diagnostic_remains_flat()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        using var db = new ReleaseTestContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(); await new CentCurriculumImporter(db).Import();
        var user = new CentUser { Email = "practice@example.invalid", NormalizedEmail = "PRACTICE@EXAMPLE.INVALID" };
        db.Users.Add(user); await db.SaveChangesAsync();
        var practice = new CentPractice(db, new CentAccess(db)); var request = Guid.NewGuid();
        var topic = CurriculumIdentity.Id("topic:reading/updates-2026-r1.html#main-idea");
        JsonElement AsJson(object value) => JsonSerializer.SerializeToElement(value, CentExamJson.Options);
        var started = AsJson(await practice.Start(user.Id, "practice", request, default, "reading", topic, "passage-v1"));
        Assert.Equal("passage-v1", started.GetProperty("contentContract").GetString());
        var questions = started.GetProperty("questions").EnumerateArray().ToArray();
        var passages = started.GetProperty("passages").EnumerateArray().ToArray(); Assert.Single(passages);
        Assert.Equal(6, questions.Length); // One independent item plus the whole five-question group.
        foreach (var q in questions.Where(q => q.GetProperty("passageId").ValueKind == JsonValueKind.String))
            Assert.Contains(passages, p => p.GetProperty("id").GetString() == q.GetProperty("passageId").GetString());
        Assert.DoesNotContain("correctAnswer", started.GetRawText()); Assert.DoesNotContain("explanation", started.GetRawText());
        Assert.Equal(started.GetRawText(), AsJson(await practice.Start(user.Id, "practice", request, default, "reading", topic, "passage-v1")).GetRawText());
        await Assert.ThrowsAsync<CentException>(() => practice.Start(user.Id, "practice", request, default, "reading", topic));
        var legacy = AsJson(await practice.Start(user.Id, "diagnostic", Guid.NewGuid(), default, "math"));
        Assert.Empty(legacy.GetProperty("passages").EnumerateArray());
        Assert.All(legacy.GetProperty("questions").EnumerateArray(), q => Assert.Equal(4, q.GetProperty("options").GetArrayLength()));
        var id = started.GetProperty("id").GetGuid();
        var keys = await db.Questions.ToDictionaryAsync(q => q.Id, q => q.CorrectAnswer);
        var outcome = AsJson(await practice.Submit(user.Id, id, questions.ToDictionary(q => q.GetProperty("id").GetGuid(), q => (int?)keys[q.GetProperty("id").GetGuid()]), default));
        Assert.Equal(100, outcome.GetProperty("result").GetProperty("percentage").GetInt32());
        db.ChangeTracker.Clear();
        var saved = (await db.Attempts.FindAsync(id))!.ResultJson;
        await practice.Submit(user.Id, id, [], default);
        Assert.Equal(saved, (await db.Attempts.FindAsync(id))!.ResultJson);
        await Assert.ThrowsAsync<CentException>(() => practice.Result(Guid.NewGuid(), id, default));
    }
    [Fact]
    public async Task Adding_release_topics_does_not_change_legacy_mastery_denominators()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        using var db = new ReleaseTestContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(); await new CentCurriculumImporter(db).Import();
        var learning = new CentLearning(db);
        var legacy = JsonSerializer.SerializeToElement(await learning.Mastery(Guid.NewGuid(), default), CentExamJson.Options);
        var current = JsonSerializer.SerializeToElement(await learning.Mastery(Guid.NewGuid(), default, Package().Release!.SyllabusRevisionId), CentExamJson.Options);
        Assert.Equal(190, legacy.GetProperty("courses").EnumerateArray().Sum(c => c.GetProperty("totalTopics").GetInt32()));
        Assert.Equal(38, current.GetProperty("courses").EnumerateArray().Sum(c => c.GetProperty("totalTopics").GetInt32()));
        Assert.Equal(Package().Release!.SyllabusRevisionId, current.GetProperty("syllabusRevisionId").GetString());
        await Assert.ThrowsAsync<CentException>(() => learning.Mastery(Guid.NewGuid(), default, "unknown"));
    }
    [Fact]
    public void Offline_validator_exports_import_ready_manifest_without_database()
    {
        var directory = Environment.GetEnvironmentVariable("CENT_CONTENT_EXPORT_DIR") ?? Path.Combine(Path.GetTempPath(), "cent-content-review", Guid.NewGuid().ToString("N"));
        Cent.Curriculum.OfflineRelease.Verify(directory);
        var manifest = CentExamJson.Read<ExamContentManifest>(File.ReadAllText(Path.Combine(directory, "exam-manifest.json")));
        Assert.Equal(Package().Questions.Count(q => q.Origin == "authored-2026-reviewed"), manifest.Questions.Length);
    }
}

// SQL Server semantics are not claimed. Existing Cent SQLite fixtures use UTC ticks
// to exercise application queries; this class lives only in the module-only test project.
public sealed class ReleaseTestContext(DbContextOptions<CentDbContext> options) : CentDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties()) {
                if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
                if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(v => v.HasValue ? v.Value.UtcTicks : null, v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null));
            }
    }
}
