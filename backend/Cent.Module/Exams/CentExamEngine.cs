using System.Text.Json.Serialization;

namespace Cent.Module.Exams;

public sealed record ExamCommand(Guid CommandId, Guid SectionId, int ExpectedRevision,
    Guid? QuestionId = null, string? SelectedOptionId = null, bool ConfirmForfeitRemainingTime = false)
{
    // Omission is different from an explicit null (clear) on the wire.
    [JsonIgnore] public bool SelectionWasProvided { get; init; } = true;
}
public sealed record ExamReceipt(Guid CommandId, bool Accepted, int AppliedRevision, DateTimeOffset AcceptedAt,
    Guid? QuestionId = null, string? SelectedOptionId = null, Guid? ClosedSectionId = null, string? ClosureReason = null);
public sealed record ExamSectionResult(Guid SectionId, string SubjectId, ExamCounts Counts);
public sealed record ExamResult(int ContractVersion, Guid AttemptId, string ExamVersionId, string RulesSnapshotId,
    string ScoringPolicyId, string Status, string FinalizationReason, DateTimeOffset EffectiveEndedAt,
    string? ResultBasis, ExamCounts Totals, ExamSectionResult[] Sections,
    string? OfficialNormalisedScore = null, string NormalisationStatus = "not_available_to_itatest");

public static class CentExamEngine
{
    public static void Activate(CentExamRun run, CentAttemptSection section, DateTimeOffset at)
    {
        section.State = "ACTIVE"; section.ActivatedAt = at; section.Deadline = at.AddSeconds(section.DurationSeconds);
        run.Mock.ActiveSectionId = section.Id; run.Mock.ActiveDeadline = section.Deadline;
    }
    public static void Reconcile(CentExamRun run, DateTimeOffset now)
    {
        var lastObservation = run.Sections.Select(s => s.ProcessedAt ?? s.ActivatedAt ?? DateTimeOffset.MinValue)
            .Concat(run.Receipts.Select(r => CentExamJson.Read<ExamReceipt>(r.ReceiptJson).AcceptedAt)).Max();
        CentModule.Check(now >= lastObservation, "Server clock moved backwards; exam state is retained.", 503, "temporarily_unavailable");
        if (run.Mock.Status == "SUBMITTING") { Finalize(run); return; }
        while (run.Mock.Status == "ACTIVE" && run.Mock.ActiveDeadline <= now) {
            var section = run.Sections.Single(s => s.Id == run.Mock.ActiveSectionId);
            Close(run, section, "CLOSED_TIMEOUT", section.Deadline!.Value, now);
        }
    }
    public static ExamReceipt Apply(CentExamRun run, string action, ExamCommand command, DateTimeOffset now)
    {
        CentModule.Check(command.CommandId != Guid.Empty && command.SectionId != Guid.Empty && command.ExpectedRevision > 0,
            "Command identity, section and revision are required.");
        CentModule.Check(action is "answer" or "advance" or "complete", "Unknown exam action.");
        var hash = CentModule.Hash(CentExamJson.Write(new { action, command.SectionId, command.ExpectedRevision,
            command.QuestionId, command.SelectedOptionId, command.ConfirmForfeitRemainingTime, command.SelectionWasProvided }));
        var existing = run.Receipts.SingleOrDefault(r => r.CommandId == command.CommandId);
        var requestedSectionTimedOut = run.Sections.Any(s => s.Id == command.SectionId && s.State == "ACTIVE" && s.Deadline <= now);
        // Reconciliation is durable even when a new input is rejected.
        Reconcile(run, now);
        if (existing is not null) {
            CentModule.Check(existing.PayloadHash == hash, "Command ID was used with another payload.", 409, "command_conflict");
            return CentExamJson.Read<ExamReceipt>(existing.ReceiptJson);
        }
        CentModule.Check(!requestedSectionTimedOut, "This section reached its server deadline.", 409, "section_closed");
        CentModule.Check(run.Mock.Status == "ACTIVE", "This attempt is closed.", 409, "section_closed");
        var section = run.Sections.SingleOrDefault(s => s.Id == command.SectionId);
        CentModule.Check(section is not null && section.State == "ACTIVE" && run.Mock.ActiveSectionId == section.Id,
            "Only the active section can be changed.", 409, "section_not_active");
        CentModule.Check(command.ExpectedRevision == run.Mock.Revision, "Reload the accepted state.", 409, "stale_revision");
        Guid? questionId = null;
        if (action == "answer") {
            CentModule.Check(command.SelectionWasProvided, "selectedOptionId must be supplied; null clears an answer.", 400, "invalid_answer");
            var row = run.Questions.SingleOrDefault(q => q.QuestionId == command.QuestionId);
            var snapshot = row is null ? null : CentExamJson.Read<AssignedExamQuestion>(row.SnapshotJson);
            CentModule.Check(snapshot is not null && snapshot.SectionId == section!.Id &&
                (command.SelectedOptionId is null || snapshot.Question.Options.Any(o => o.Id == command.SelectedOptionId)),
                "Question or option is not assigned to this section.", 400, "invalid_answer");
            row!.Answer = command.SelectedOptionId is null ? null : Array.FindIndex(snapshot!.Question.Options, o => o.Id == command.SelectedOptionId);
            run.Mock.Revision++; questionId = row.QuestionId;
        } else if (action == "advance") {
            CentModule.Check(section!.Order < run.Sections.Count, "Use complete for the final section.", 409, "final_section_required");
            CentModule.Check(command.ConfirmForfeitRemainingTime, "Confirm loss of remaining time and return access.", 400, "confirmation_required");
            Close(run, section, "CLOSED_EARLY", now, now);
        } else {
            CentModule.Check(section!.Order == run.Sections.Count, "Complete is available only in the final section.", 409, "final_section_required");
            Close(run, section, "CLOSED_SUBMISSION", now, now);
        }
        var receipt = new ExamReceipt(command.CommandId, true, run.Mock.Revision, now, questionId,
            action == "answer" ? command.SelectedOptionId : null, action == "answer" ? null : section!.Id,
            action == "advance" ? "early_advance" : action == "complete" ? "participant_submit" : null);
        run.Receipts.Add(new CentExamCommandReceipt { AttemptId = run.Attempt.Id, CommandId = command.CommandId,
            PayloadHash = hash, ReceiptJson = CentExamJson.Write(receipt) });
        return receipt;
    }
    private static void Close(CentExamRun run, CentAttemptSection section, string state, DateTimeOffset effectiveAt, DateTimeOffset processedAt)
    {
        section.State = state; section.ClosedAt = effectiveAt; section.ProcessedAt = processedAt;
        run.Mock.Revision++;
        var next = run.Sections.SingleOrDefault(s => s.Order == section.Order + 1);
        if (next is not null) { Activate(run, next, effectiveAt); return; }
        run.Mock.ActiveSectionId = null; run.Mock.ActiveDeadline = null;
        run.Mock.EffectiveEndedAt = effectiveAt;
        run.Mock.FinalizationReason = state == "CLOSED_TIMEOUT" ? "final_section_timeout" : "participant_submit";
        run.Mock.Status = "SUBMITTING";
        // Small, deterministic calculation in the same transaction: crash rolls back the
        // entire closure or retains the one terminal result, never an acknowledged partial result.
        Finalize(run);
    }
    public static void Finalize(CentExamRun run)
    {
        if (run.Mock.Status != "SUBMITTING" || run.Mock.ResultJson is not null) return;
        var rules = run.Rules;
        var expired = run.Mock.FinalizationReason == "final_section_timeout";
        CentModule.Check(rules.Definition.SubmissionPolicyId is "expired-feedback-v1" or "expired-counts-only-v1",
            "Submission policy is not selected.", 409, "exam_unavailable");
        var scored = !expired || rules.Definition.SubmissionPolicyId == "expired-feedback-v1";
        var results = run.Sections.OrderBy(s => s.Order).Select(s => {
            var rows = run.Questions.Select(r => (Row: r, Q: CentExamJson.Read<AssignedExamQuestion>(r.SnapshotJson)))
                .Where(r => r.Q.SectionId == s.Id).ToArray();
            var correct = rows.Count(r => r.Row.Answer is { } a && r.Q.Question.Options[a].Id == r.Q.Question.CorrectOptionId);
            var answered = rows.Count(r => r.Row.Answer is not null);
            return new ExamSectionResult(s.Id, s.SubjectId, CentExamScoring.Calculate(s.QuestionCount, correct, answered - correct, scored));
        }).ToArray();
        var status = expired ? "EXPIRED" : "COMPLETED";
        var totals = CentExamScoring.Calculate(results.Sum(s => s.Counts.QuestionCount),
            results.Sum(s => s.Counts.CorrectCount), results.Sum(s => s.Counts.IncorrectCount), scored);
        run.Mock.ResultJson = CentExamJson.Write(new ExamResult(1, run.Attempt.Id, rules.Definition.ExamVersionId,
            rules.RulesSnapshotId, rules.Definition.Scoring.PolicyId, status, run.Mock.FinalizationReason!,
            run.Mock.EffectiveEndedAt!.Value, scored ? "itatest_raw_mock" : null, totals, results));
        run.Mock.Status = status;
        // Legacy SubmittedAt/ResultJson remain null. Existing mastery and history readers
        // consequently never interpret a new mock snapshot as a legacy assessment.
    }
}
