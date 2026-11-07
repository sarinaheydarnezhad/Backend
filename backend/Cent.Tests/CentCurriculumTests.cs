using System.Text.Json;
using Cent.Module;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cent.Tests;

public sealed class CentCurriculumTests
{
    [Fact]
    public async Task Public_curriculum_exposes_source_outlines_without_lesson_text_or_answer_keys()
    {
        using var factory = new CentFactory();
        using var client = factory.CreateClient();
        using (var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(factory.Services)) {
            var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<CentDbContext>(scope.ServiceProvider);
            await new CentCurriculumImporter(db).Import();
        }
        var response = await client.GetAsync("/api/cent/v1/curriculum");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(5, root.GetProperty("courses").GetArrayLength());
        Assert.Equal(39, root.GetProperty("documents").GetArrayLength());
        Assert.Equal(190, root.GetProperty("topics").GetArrayLength());
        foreach (var privateKey in new[] { "text", "html", "originalHtml", "plainText", "correctAnswer", "explanation", "options" })
            Assert.DoesNotContain("\"" + privateKey + "\":", json);
        var id = root.GetProperty("documents")[0].GetProperty("id").GetGuid();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/cent/v1/documents/{id}/source-sections")).StatusCode);
    }

    [Fact]
    public async Task Subject_progress_uses_all_topics_and_supports_partial_skill_mastery()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var db = new SqliteCentContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new CentUser { Email = "percent@test", NormalizedEmail = "PERCENT@TEST", Name = "Learner" };
        var source = new CentSource { Id = Guid.NewGuid(), ImportKey = "source", CourseCode = "math" };
        var chapter = new CentChapter { Id = Guid.NewGuid(), ImportKey = "chapter", SourceId = source.Id, CourseCode = "math" };
        db.AddRange(user, new CentCourse { Code = "math", Title = "Math" }, source, chapter);
        var attempt = new CentAttempt { UserId = user.Id, RequestId = Guid.NewGuid(), SubmittedAt = DateTimeOffset.UtcNow };
        db.Add(attempt);
        for (int t = 0; t < 10; t++) {
            var topic = new CentTopic { Id = Guid.NewGuid(), ImportKey = "topic-" + t, ChapterId = chapter.Id, CourseCode = "math", Title = "Skill " + t };
            db.Add(topic);
            for (int q = 0; q < 4; q++) {
                var question = new CentQuestion { TopicId = topic.Id, Subject = "math", Active = true, CorrectAnswer = 0, Topic = topic.Title };
                db.Add(question);
                if (t < 3) db.Add(new CentAttemptQuestion { AttemptId = attempt.Id, Position = t * 4 + q, QuestionId = question.Id, Answer = 0,
                    SnapshotJson = JsonSerializer.Serialize(new QuestionSnapshot(question.Id, "math", topic.Title, "medium", "Prompt", ["Yes", "No"], 0, "Explanation", topic.Id)) });
            }
        }
        await db.SaveChangesAsync();
        JsonElement Report(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("courses")[0];
        var first = Report(await new CentLearning(db).Mastery(user.Id, default));
        Assert.Equal(30, first.GetProperty("confirmedProgressPercentage").GetInt32());
        Assert.Equal(30, first.GetProperty("coveragePercentage").GetInt32());
        foreach (var row in await db.AttemptQuestions.Where(q => q.Position < 2).ToListAsync()) row.Answer = 1;
        await db.SaveChangesAsync();
        var second = Report(await new CentLearning(db).Mastery(user.Id, default));
        Assert.Equal(25, second.GetProperty("confirmedProgressPercentage").GetInt32());
        Assert.Contains(second.GetProperty("topics").EnumerateArray(), t => t.GetProperty("masteryPercentage").ValueKind == JsonValueKind.Number && t.GetProperty("masteryPercentage").GetInt32() == 50);
    }

    [Fact]
    public async Task Source_import_is_complete_traceable_and_idempotent()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var db = new SqliteCentContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var importer = new CentCurriculumImporter(db);
        var report = await importer.Import();
        Assert.Equal(39, report.Sources);
        Assert.Equal(819, report.Questions);
        Assert.True((await importer.Import()).AlreadyImported);
        Assert.Equal(819, await db.Questions.CountAsync());
        Assert.Equal(5, await db.Courses.CountAsync());
        Assert.Equal(190, await db.Topics.CountAsync());
        Assert.All(await db.Topics.ToArrayAsync(), t => Assert.True(db.Questions.Any(q => q.TopicId == t.Id)));
        Assert.All(await db.Questions.ToArrayAsync(), q => {
            Assert.Contains(q.Difficulty, new[] { "easy", "medium", "hard" });
            Assert.NotNull(q.SourceSectionId); Assert.NotNull(q.TopicId); Assert.NotEmpty(q.Explanation);
        });
        Assert.Single(await db.SourceExercises.Where(e => e.Status == "quarantined").ToArrayAsync());
        Assert.Contains(await db.Topics.ToArrayAsync(), t => t.ImportKey == "math.algebraic-expressions");
    }

    [Fact]
    public async Task Assessments_filter_skills_hide_keys_and_keep_distinct_owner_scoped_mastery()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var db = new SqliteCentContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await new CentCurriculumImporter(db).Import();
        var user = new CentUser { Email = "learner@test", NormalizedEmail = "LEARNER@TEST", Name = "Learner" };
        var other = new CentUser { Email = "other@test", NormalizedEmail = "OTHER@TEST", Name = "Other" };
        db.AddRange(user, other); await db.SaveChangesAsync();
        var topic = await db.Topics.SingleAsync(t => t.ImportKey == "math.algebraic-expressions");
        var practice = new CentPractice(db, new CentAccess(db));
        var learning = new CentLearning(db);
        async Task Run(string kind, bool correct = true) {
            var started = JsonSerializer.SerializeToElement(await practice.Start(user.Id, kind, Guid.NewGuid(), default, "math", topic.Id), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.DoesNotContain("correctAnswer", started.GetRawText());
            var questions = started.GetProperty("questions").EnumerateArray().ToArray();
            Assert.All(questions, q => Assert.Equal(topic.Id, q.GetProperty("topicId").GetGuid()));
            var keys = await db.Questions.ToDictionaryAsync(q => q.Id, q => q.CorrectAnswer);
            var answers = questions.ToDictionary(q => q.GetProperty("id").GetGuid(), q => (int?)(correct ? keys[q.GetProperty("id").GetGuid()] : (keys[q.GetProperty("id").GetGuid()] + 1) % 4));
            var id = started.GetProperty("id").GetGuid();
            await practice.Submit(user.Id, id, answers, default);
            // Idempotent submission cannot rewrite persisted evidence.
            await practice.Submit(user.Id, id, new(), default);
            await Assert.ThrowsAsync<CentException>(() => practice.Result(other.Id, id, default));
        }
        JsonElement AsJson(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        JsonElement Math(JsonElement value) => value.GetProperty("courses").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "math");
        await Run("practice", correct: false);
        var practiceMath = Math(AsJson(await learning.Mastery(user.Id, default)));
        Assert.Equal(1, practiceMath.GetProperty("assessedTopics").GetInt32());
        Assert.Equal(0, practiceMath.GetProperty("topics").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == topic.Id).GetProperty("masteryPercentage").GetInt32());
        await Run("practice");
        var improved = Math(AsJson(await learning.Mastery(user.Id, default))).GetProperty("topics").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == topic.Id);
        Assert.Equal(100, improved.GetProperty("masteryPercentage").GetInt32());
        await Run("diagnostic");
        var first = Math(AsJson(await learning.Mastery(user.Id, default)));
        var skill = first.GetProperty("topics").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == topic.Id);
        Assert.Equal(100, skill.GetProperty("masteryPercentage").GetInt32());
        Assert.Equal(1, first.GetProperty("assessedTopics").GetInt32());
        Assert.True(first.GetProperty("confirmedProgressPercentage").GetInt32() < 100);
        await Run("diagnostic");
        var repeated = Math(AsJson(await learning.Mastery(user.Id, default))).GetProperty("topics").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == topic.Id);
        Assert.Equal(skill.GetProperty("evidenceQuestions").GetInt32(), repeated.GetProperty("evidenceQuestions").GetInt32());
        Assert.Equal(0, Math(AsJson(await learning.Mastery(other.Id, default))).GetProperty("assessedTopics").GetInt32());
    }
}
