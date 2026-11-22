using System.Text.Json;
using Cent.Module;
using Cent.Module.Exams;

namespace Cent.Exam.Tests;

public sealed class ExamEngineTests
{
    [Fact]
    public void Both_blueprints_have_exact_order_quotas_and_independent_timing()
    {
        var old = CentExamBlueprints.Historical; var current = CentExamBlueprints.Current;
        old.Validate(); current.Validate();
        Assert.Equal(55, old.TotalQuestions); Assert.Equal(6600, old.TotalDurationSeconds);
        Assert.Equal(65, current.TotalQuestions); Assert.Equal(8400, current.TotalDurationSeconds);
        Assert.Equal(["reading", "math", "logic", "biology", "chemistry", "physics"], current.Sections.Select(s => s.SubjectId));
        Assert.Equal([10, 15, 15, 10, 10, 5], current.Sections.Select(s => s.QuestionCount));
        Assert.Equal([1800, 1800, 1800, 1200, 1200, 600], current.Sections.Select(s => s.DurationSeconds));
        Assert.Equal(current.Sections.Skip(1).Select(s => s.SubjectId), old.Sections.Select(s => s.SubjectId));
        Assert.Null(old.SubmissionPolicyId); Assert.Null(current.SyllabusRevisionId);
    }
    [Theory]
    [InlineData(65, 41, 14, 150, "37.50", "74.55", "84.62")]
    [InlineData(65, 0, 65, -65, "-16.25", "0.00", "100.00")]
    [InlineData(65, 0, 0, 0, "0.00", null, "0.00")]
    [InlineData(55, 55, 0, 220, "55.00", "100.00", "100.00")]
    [InlineData(15, 10, 3, 37, "9.25", "76.92", "86.67")]
    [InlineData(0, 0, 0, 0, "0.00", null, null)]
    public void Exact_scoring_keeps_accuracy_and_completion_separate(int n, int c, int i, int quarters, string raw, string? accuracy, string? completion)
    {
        var r = CentExamScoring.Calculate(n, c, i);
        Assert.Equal(quarters, r.RawQuarterPoints); Assert.Equal(raw, r.RawScore);
        Assert.Equal(accuracy, r.Accuracy.DisplayPercent); Assert.Equal(completion, r.CompletionRate.DisplayPercent);
        Assert.Equal(n, r.CorrectCount + r.IncorrectCount + r.UnansweredCount);
    }
    [Fact]
    public void Assembly_freezes_rules_options_order_and_one_deduplicated_passage()
    {
        var f = new ExamFixture(); var run = f.Run();
        Assert.Equal(65, run.Questions.Count); Assert.Equal(6, run.Sections.Count);
        Assert.Single(run.Rules.Passages); Assert.Equal(2, run.Rules.Groups.Length);
        Assert.Equal(Enumerable.Range(0, 65), run.Questions.Select(q => q.Position));
        var frozen = run.Mock.RulesSnapshotJson;
        f.Definition.Sections[0] = f.Definition.Sections[0] with { DurationSeconds = 1 };
        f.Manifest.Questions[0].Options[0] = new("a", "Changed live text");
        Assert.Equal(1800, run.Rules.Definition.Sections[0].DurationSeconds);
        Assert.DoesNotContain("Changed live text", run.Questions[0].SnapshotJson);
        Assert.Equal(frozen, run.Mock.RulesSnapshotJson);
    }
    [Theory]
    [InlineData("four-options")]
    [InlineData("shortfall")]
    [InlineData("missing-passage")]
    [InlineData("broken-group")]
    [InlineData("cross-section")]
    [InlineData("unreviewed")]
    public void Ineligible_or_broken_content_fails_before_timed_attempt(string error)
    {
        var f = new ExamFixture(); var m = f.Manifest;
        if (error == "four-options") m.Questions[0] = m.Questions[0] with { Options = m.Questions[0].Options[..4] };
        if (error == "shortfall") m = m with { Questions = m.Questions.Where(q => q.SubjectId != "physics" || q != m.Questions.Last()).ToArray() };
        if (error == "missing-passage") m = m with { Passages = [] };
        if (error == "broken-group") m.Groups[0].QuestionRevisionIds[0] = "missing-question";
        if (error == "cross-section") m.Questions[0] = m.Questions[0] with { SubjectId = "math" };
        if (error == "unreviewed") m = m with { Eligibility = m.Eligibility.Select(e => e with { Status = "unreviewed" }).ToArray() };
        var ex = Assert.Throws<CentException>(() => CentExamAssembly.Assemble(f.Owner, Guid.NewGuid(), f.Definition, m, "test", f.Clock.Now));
        Assert.Equal("exam_not_ready", ex.Code);
    }
    [Fact]
    public void Early_advancement_requires_confirmation_forfeits_time_and_cannot_reopen()
    {
        var f = new ExamFixture(); var run = f.Run(); var reading = run.Sections[0];
        f.Clock.Now = f.Clock.Now.AddMinutes(20);
        var command = new ExamCommand(Guid.NewGuid(), reading.Id, 1);
        Assert.Equal("confirmation_required", Assert.Throws<CentException>(() => CentExamEngine.Apply(run, "advance", command, f.Clock.Now)).Code);
        Assert.Equal("ACTIVE", reading.State);
        var receipt = CentExamEngine.Apply(run, "advance", command with { ConfirmForfeitRemainingTime = true }, f.Clock.Now);
        Assert.Equal("CLOSED_EARLY", reading.State); Assert.Equal(f.Clock.Now.AddMinutes(30), run.Sections[1].Deadline);
        Assert.Equal("section_not_active", Assert.Throws<CentException>(() => CentExamEngine.Apply(run, "advance", command with { CommandId = Guid.NewGuid(), ExpectedRevision = 2 }, f.Clock.Now)).Code);
        Assert.Equal(2, receipt.AppliedRevision);
    }
    [Fact]
    public void Exact_deadline_rejects_late_answer_and_catches_up_without_reset()
    {
        var f = new ExamFixture(); var run = f.Run(); var q = run.Questions[0];
        var start = f.Clock.Now; var deadline = start.AddMinutes(30);
        var command = new ExamCommand(Guid.NewGuid(), run.Sections[0].Id, 1, q.QuestionId, "b");
        CentExamEngine.Apply(run, "answer", command, deadline.AddTicks(-1));
        Assert.Equal(1, q.Answer);
        Assert.Equal("section_closed", Assert.Throws<CentException>(() => CentExamEngine.Apply(run, "answer", command with { CommandId = Guid.NewGuid(), ExpectedRevision = 2, SelectedOptionId = "c" }, deadline)).Code);
        Assert.Equal(1, q.Answer); Assert.Equal(deadline, run.Sections[1].ActivatedAt);
        CentExamEngine.Reconcile(run, start.AddMinutes(95));
        Assert.Equal("biology", run.Sections.Single(s => s.Id == run.Mock.ActiveSectionId).SubjectId);
        Assert.Equal(start.AddMinutes(110), run.Mock.ActiveDeadline);
        Assert.All(run.Sections.Take(3), s => Assert.Equal("CLOSED_TIMEOUT", s.State));
        Assert.Equal(deadline, run.Sections[0].ClosedAt);
    }
    [Theory]
    [InlineData("expired-feedback-v1", "0.00")]
    [InlineData("expired-counts-only-v1", null)]
    public void Browser_closure_expiry_uses_frozen_profile_and_full_denominator(string policy, string? raw)
    {
        var f = new ExamFixture(profile: policy); var run = f.Run();
        CentExamEngine.Reconcile(run, f.Clock.Now.AddMinutes(141));
        Assert.Equal("EXPIRED", run.Mock.Status); Assert.Null(run.Mock.ActiveSectionId);
        Assert.All(run.Sections, s => Assert.Equal("CLOSED_TIMEOUT", s.State));
        Assert.Equal(f.Clock.Now.AddMinutes(140), run.Mock.EffectiveEndedAt);
        var result = CentExamJson.Read<ExamResult>(run.Mock.ResultJson!);
        Assert.Equal(65, result.Totals.UnansweredCount); Assert.Equal(raw, result.Totals.RawScore);
        Assert.Null(result.Totals.Accuracy.DisplayPercent); Assert.Equal("0.00", result.Totals.CompletionRate.DisplayPercent);
        Assert.Null(result.OfficialNormalisedScore);
        var saved = run.Mock.ResultJson; CentExamEngine.Reconcile(run, f.Clock.Now.AddDays(3)); Assert.Equal(saved, run.Mock.ResultJson);
    }
    [Fact]
    public void Complete_is_final_section_only_and_result_retry_never_rescores()
    {
        var f = new ExamFixture(); var run = f.Run();
        Assert.Equal("final_section_required", Assert.Throws<CentException>(() => CentExamEngine.Apply(run, "complete", new(Guid.NewGuid(), run.Sections[0].Id, 1), f.Clock.Now)).Code);
        foreach (var section in run.Sections.Take(5))
            CentExamEngine.Apply(run, "advance", new(Guid.NewGuid(), section.Id, run.Mock.Revision, ConfirmForfeitRemainingTime: true), f.Clock.Now);
        var final = run.Sections.Last();
        Assert.Equal("final_section_required", Assert.Throws<CentException>(() => CentExamEngine.Apply(run, "advance", new(Guid.NewGuid(), final.Id, run.Mock.Revision, ConfirmForfeitRemainingTime: true), f.Clock.Now)).Code);
        var command = new ExamCommand(Guid.NewGuid(), final.Id, run.Mock.Revision);
        var receipt = CentExamEngine.Apply(run, "complete", command, f.Clock.Now);
        var result = run.Mock.ResultJson;
        Assert.Equal("COMPLETED", run.Mock.Status); Assert.Equal("CLOSED_SUBMISSION", final.State);
        Assert.Equal(receipt, CentExamEngine.Apply(run, "complete", command, f.Clock.Now.AddDays(1)));
        Assert.Equal(result, run.Mock.ResultJson); Assert.Null(run.Attempt.SubmittedAt); Assert.Null(run.Attempt.ResultJson);
    }
    [Fact]
    public void Clear_stale_revision_invalid_option_and_idempotency_are_distinct()
    {
        var f = new ExamFixture(); var run = f.Run(); var q = run.Questions[0];
        var command = new ExamCommand(Guid.NewGuid(), run.Sections[0].Id, 1, q.QuestionId, "b");
        var receipt = CentExamEngine.Apply(run, "answer", command, f.Clock.Now);
        Assert.Equal(receipt, CentExamEngine.Apply(run, "answer", command, f.Clock.Now.AddSeconds(1)));
        Assert.Equal(2, run.Mock.Revision);
        Assert.Equal("command_conflict", Assert.Throws<CentException>(() => CentExamEngine.Apply(run, "answer", command with { SelectedOptionId = "a" }, f.Clock.Now)).Code);
        Assert.Equal("stale_revision", Assert.Throws<CentException>(() => CentExamEngine.Apply(run, "answer", command with { CommandId = Guid.NewGuid() }, f.Clock.Now)).Code);
        Assert.Equal("invalid_answer", Assert.Throws<CentException>(() => CentExamEngine.Apply(run, "answer", command with { CommandId = Guid.NewGuid(), ExpectedRevision = 2, SelectedOptionId = "z" }, f.Clock.Now)).Code);
        CentExamEngine.Apply(run, "answer", command with { CommandId = Guid.NewGuid(), ExpectedRevision = 2, SelectedOptionId = null }, f.Clock.Now);
        Assert.Null(q.Answer); Assert.Equal(3, run.Mock.Revision);
    }
    [Fact]
    public void Active_payload_has_no_key_explanation_or_future_questions()
    {
        var f = new ExamFixture(); var run = f.Run();
        var json = CentExamJson.Write(CentExamService.ActiveContent(run, f.Clock.Now));
        Assert.DoesNotContain("correctOptionId", json); Assert.DoesNotContain("explanation", json);
        Assert.DoesNotContain("Synthetic math item", json);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal(10, root.GetProperty("questions").GetArrayLength()); Assert.Equal(1, root.GetProperty("passages").GetArrayLength());
    }
    [Fact]
    public void Early_forfeiture_shortens_overall_schedule_and_answering_every_item_does_not_advance()
    {
        var f = new ExamFixture(); var run = f.Run(); var started = f.Clock.Now; var reading = run.Sections[0];
        foreach (var row in run.Questions.Take(10))
            CentExamEngine.Apply(run, "answer", new(Guid.NewGuid(), reading.Id, run.Mock.Revision, row.QuestionId, "b"), started);
        Assert.Equal(reading.Id, run.Mock.ActiveSectionId);
        CentExamEngine.Apply(run, "advance", new(Guid.NewGuid(), reading.Id, run.Mock.Revision, ConfirmForfeitRemainingTime: true), started.AddMinutes(20));
        CentExamEngine.Reconcile(run, started.AddMinutes(131));
        Assert.Equal(started.AddMinutes(130), run.Mock.EffectiveEndedAt);
        Assert.Equal("CLOSED_EARLY", reading.State);
        Assert.Equal(10, CentExamJson.Read<ExamResult>(run.Mock.ResultJson!).Totals.CorrectCount);
    }
}
