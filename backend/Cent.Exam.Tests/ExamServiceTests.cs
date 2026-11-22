using System.Reflection;
using System.Text.Json;
using Cent.Module;
using Cent.Module.Exams;
using Microsoft.EntityFrameworkCore;

namespace Cent.Exam.Tests;

public sealed class ExamServiceTests
{
    [Fact]
    public async Task Start_retry_retains_snapshot_after_definition_archival_and_configuration_change()
    {
        var f = new ExamFixture(); var request = Guid.NewGuid(); var id = await f.Start(request);
        var snapshot = f.Store.Runs[id].Mock.RulesSnapshotJson;
        f.Store.Version.Status = "ARCHIVED"; f.Store.Version.AvailableForNewStarts = false;
        f.Store.Version.ConfigurationRevision = "future-revision";
        f.Store.Version.DefinitionJson = CentExamJson.Write(CentExamBlueprints.Historical);
        f.Clock.Now = f.Clock.Now.AddMinutes(10);
        Assert.Equal(id, await f.Start(request));
        Assert.Equal(snapshot, f.Store.Runs[id].Mock.RulesSnapshotJson);
        Assert.Equal(65, f.Store.Runs[id].Questions.Count);
        Assert.Equal(f.Clock.Now.AddMinutes(20), f.Store.Runs[id].Mock.ActiveDeadline);
        var ex = await Assert.ThrowsAsync<CentException>(() => f.Service.Start(f.Owner,
            new(request, f.Definition.ExamVersionId, "different"), default));
        Assert.Equal("command_conflict", ex.Code);
    }
    [Fact]
    public async Task Reconnection_persists_due_transitions_even_when_the_late_input_is_rejected()
    {
        var f = new ExamFixture(); var id = await f.Start(); var run = f.Store.Runs[id];
        var section = run.Sections[0].Id; var q = run.Questions[0].QuestionId;
        f.Clock.Now = f.Clock.Now.AddMinutes(95);
        var error = await Assert.ThrowsAsync<CentException>(() => f.Service.Command(f.Owner, id, "answer", new(Guid.NewGuid(), section, 1, q, "a"), default));
        Assert.Equal("section_closed", error.Code);
        Assert.Equal("biology", f.Store.Runs[id].Sections.Single(s => s.Id == f.Store.Runs[id].Mock.ActiveSectionId).SubjectId);
        var deadline = f.Store.Runs[id].Mock.ActiveDeadline;
        await f.Service.Read(f.Owner, id, true, default);
        Assert.Equal(deadline, f.Store.Runs[id].Mock.ActiveDeadline);
    }
    [Fact]
    public async Task Answer_is_acknowledged_only_after_commit_and_lost_response_retry_is_safe()
    {
        var f = new ExamFixture(); var id = await f.Start(); var run = f.Store.Runs[id];
        var command = new ExamCommand(Guid.NewGuid(), run.Sections[0].Id, 1, run.Questions[0].QuestionId, "b");
        f.Store.FailNextCommit = true;
        await Assert.ThrowsAsync<IOException>(() => f.Service.Command(f.Owner, id, "answer", command, default));
        Assert.Null(f.Store.Runs[id].Questions[0].Answer); Assert.Empty(f.Store.Runs[id].Receipts);
        var saved = JsonSerializer.SerializeToElement(await f.Service.Command(f.Owner, id, "answer", command, default), CentExamJson.Options);
        Assert.True(saved.GetProperty("receipt").GetProperty("accepted").GetBoolean());
        Assert.Equal(1, f.Store.Runs[id].Questions[0].Answer);
        f.Clock.Now = f.Clock.Now.AddMinutes(31);
        var replay = JsonSerializer.SerializeToElement(await f.Service.Command(f.Owner, id, "answer", command, default), CentExamJson.Options);
        Assert.Equal(saved.GetProperty("receipt").ToString(), replay.GetProperty("receipt").ToString());
        Assert.Equal(3, replay.GetProperty("currentState").GetProperty("revision").GetInt32());
        Assert.Single(f.Store.Runs[id].Receipts);
    }
    [Fact]
    public async Task Concurrent_tabs_cannot_overwrite_or_skip_sections()
    {
        var f = new ExamFixture(); var id = await f.Start(); var run = f.Store.Runs[id];
        var command = new ExamCommand(Guid.NewGuid(), run.Sections[0].Id, 1, run.Questions[0].QuestionId, "a");
        async Task<string> Save(ExamCommand c) {
            try { await f.Service.Command(f.Owner, id, "answer", c, default); return "saved"; }
            catch (CentException ex) { return ex.Code; }
        }
        var outcomes = await Task.WhenAll(Save(command), Save(command with { CommandId = Guid.NewGuid(), SelectedOptionId = "b" }));
        Assert.Contains("saved", outcomes); Assert.Contains("stale_revision", outcomes);
        Assert.Single(f.Store.Runs[id].Receipts); Assert.Equal(2, f.Store.Runs[id].Mock.Revision);
    }
    [Fact]
    public async Task Missing_and_foreign_attempts_are_identical_and_active_result_review_are_locked()
    {
        var f = new ExamFixture(); var id = await f.Start();
        var foreign = await Assert.ThrowsAsync<CentException>(() => f.Service.Result(Guid.NewGuid(), id, default));
        var missing = await Assert.ThrowsAsync<CentException>(() => f.Service.Result(f.Owner, Guid.NewGuid(), default));
        Assert.Equal(404, foreign.Status); Assert.Equal(foreign.Code, missing.Code);
        Assert.Equal("attempt_not_finalized", (await Assert.ThrowsAsync<CentException>(() => f.Service.Result(f.Owner, id, default))).Code);
        Assert.Equal("attempt_not_finalized", (await Assert.ThrowsAsync<CentException>(() => f.Service.Review(f.Owner, id, default))).Code);
    }
    [Fact]
    public async Task Pending_policies_and_ineligible_banks_cannot_start_an_attempt()
    {
        var f = new ExamFixture(); f.Admission.IsConfigured = false;
        Assert.Equal("exam_unavailable", (await Assert.ThrowsAsync<CentException>(() => f.Start())).Code); Assert.Empty(f.Store.Runs);
        f.Admission.IsConfigured = true;
        f.Store.Content.ManifestJson = CentExamJson.Write(f.Manifest with { Passages = [] });
        Assert.Equal("exam_not_ready", (await Assert.ThrowsAsync<CentException>(() => f.Start())).Code); Assert.Empty(f.Store.Runs);
    }
    [Fact]
    public async Task Historical_format_remains_a_five_section_snapshot_with_its_own_deadlines()
    {
        var f = new ExamFixture(historical: true); var id = await f.Start();
        f.Clock.Now = f.Clock.Now.AddMinutes(111);
        await f.Service.Result(f.Owner, id, default);
        var run = f.Store.Runs[id];
        Assert.Equal(5, run.Sections.Count); Assert.Equal(55, run.Questions.Count);
        Assert.Equal(f.Clock.Now.AddMinutes(-1), run.Mock.EffectiveEndedAt);
        Assert.DoesNotContain(run.Sections, s => s.SubjectId == "reading");
    }
    [Fact]
    public async Task Completed_mixed_fixture_matches_T1_1_exact_totals_and_result_read_is_immutable()
    {
        var f = new ExamFixture(); var id = await f.Start();
        var correct = new[] { 8, 10, 9, 6, 5, 3 }; var incorrect = new[] { 1, 3, 4, 2, 3, 1 };
        for (var i = 0; i < 6; i++) {
            var section = f.Store.Runs[id].Sections[i];
            var questions = f.Store.Runs[id].Questions.Where(r => CentExamJson.Read<AssignedExamQuestion>(r.SnapshotJson).SectionId == section.Id).ToArray();
            for (var n = 0; n < correct[i] + incorrect[i]; n++)
                await f.Service.Command(f.Owner, id, "answer", new(Guid.NewGuid(), section.Id,
                    f.Store.Runs[id].Mock.Revision, questions[n].QuestionId, n < correct[i] ? "b" : "a"), default);
            await f.Service.Command(f.Owner, id, i == 5 ? "complete" : "advance", new(Guid.NewGuid(), section.Id,
                f.Store.Runs[id].Mock.Revision, ConfirmForfeitRemainingTime: true), default);
        }
        var saved = f.Store.Runs[id].Mock.ResultJson;
        f.Store.Content.ManifestJson = "{}"; f.Store.Version.Status = "ARCHIVED";
        var result = JsonSerializer.SerializeToElement(await f.Service.Result(f.Owner, id, default), CentExamJson.Options);
        Assert.Equal("37.50", result.GetProperty("rawScore").GetString()); Assert.Equal(150, result.GetProperty("rawQuarterPoints").GetInt32());
        Assert.Equal("74.55", result.GetProperty("accuracy").GetProperty("displayPercent").GetString());
        Assert.Equal("84.62", result.GetProperty("completionRate").GetProperty("displayPercent").GetString());
        Assert.Equal(saved, f.Store.Runs[id].Mock.ResultJson);
        Assert.Null(f.Store.Runs[id].Attempt.ResultJson); Assert.Null(f.Store.Runs[id].Attempt.SubmittedAt);
    }
    [Fact]
    public void Model_is_Cent_only_and_published_snapshots_cannot_be_rewritten_without_any_DB_connection()
    {
        using var db = new CentDbContext(new DbContextOptionsBuilder<CentDbContext>()
            .UseSqlServer("Server=offline.invalid;Database=NeverConnect;Integrated Security=true;TrustServerCertificate=true").Options);
        Assert.All(db.Model.GetEntityTypes(), e => Assert.StartsWith("cent_", e.GetTableName()));
        var f = new ExamFixture();
        var version = f.Store.Version; db.Attach(version); version.DefinitionJson = "changed";
        Assert.Throws<InvalidOperationException>(() => CentExamIntegrity.Validate(db.ChangeTracker));
        db.ChangeTracker.Clear(); var run = f.Run(); db.Attach(run.Mock); run.Mock.RulesSnapshotJson = "changed";
        Assert.Throws<InvalidOperationException>(() => CentExamIntegrity.Validate(db.ChangeTracker));
        db.ChangeTracker.Clear(); var section = run.Sections[0]; db.Attach(section); section.DurationSeconds++;
        Assert.Throws<InvalidOperationException>(() => CentExamIntegrity.Validate(db.ChangeTracker));
        db.ChangeTracker.Clear(); var terminal = f.Run();
        CentExamEngine.Reconcile(terminal, f.Clock.Now.AddMinutes(141));
        db.Attach(terminal.Mock); terminal.Mock.ResultJson = "recalculated";
        Assert.Throws<InvalidOperationException>(() => CentExamIntegrity.Validate(db.ChangeTracker));
        db.ChangeTracker.Clear(); var snapshot = f.Run(); db.Attach(snapshot.Mock); db.Attach(snapshot.Questions[0]);
        snapshot.Questions[0].SnapshotJson = "edited question";
        Assert.Throws<InvalidOperationException>(() => CentExamIntegrity.Validate(db.ChangeTracker));
    }
    [Fact]
    public void Legacy_scoring_and_stored_result_payload_remain_the_original_60_percent_contract()
    {
        var questions = Enumerable.Range(0, 20).Select(i => new QuestionSnapshot(Guid.NewGuid(), "math", "Test", "medium", "Question", ["A", "B", "C", "D"], 1, "Explanation")).ToArray();
        var answers = questions.Take(16).Select((q, i) => (q.Id, Answer: i < 12 ? (int?)1 : 0)).ToDictionary(x => x.Id, x => x.Answer);
        var method = typeof(CentPractice).GetMethod("Score", BindingFlags.Static | BindingFlags.NonPublic)!;
        var scored = method.Invoke(null, [Guid.NewGuid(), DateTimeOffset.UnixEpoch, questions, answers])!;
        var json = JsonSerializer.SerializeToElement(scored, CentExamJson.Options);
        Assert.Equal(60, json.GetProperty("percentage").GetInt32()); Assert.Equal(4, json.GetProperty("unanswered").GetInt32());
        Assert.False(json.TryGetProperty("rawScore", out _));
        const string stored = "{\"total\":20,\"correct\":12,\"incorrect\":4,\"unanswered\":4,\"percentage\":60}";
        var old = new CentAttempt { Kind = "simulator", ResultJson = stored, SubmittedAt = DateTimeOffset.UnixEpoch };
        var f = new ExamFixture(); var run = f.Run(); CentExamEngine.Reconcile(run, f.Clock.Now.AddDays(1));
        Assert.Equal(stored, old.ResultJson);
    }
}
