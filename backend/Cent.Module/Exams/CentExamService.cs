using System.Text.Json;

namespace Cent.Module.Exams;

public interface ICentMockAdmission
{
    bool IsConfigured { get; }
    Task<string> AuthorizeStart(Guid owner, ExamDefinition definition, CancellationToken ct);
    Task AuthorizeReview(Guid owner, Guid attempt, CancellationToken ct);
}
// Fail-closed alternative retained for hosts that have not selected an access policy.
public sealed class PendingCentMockAdmission : ICentMockAdmission
{
    public bool IsConfigured => false;
    public Task<string> AuthorizeStart(Guid owner, ExamDefinition definition, CancellationToken ct) =>
        throw new CentException(409, "exam_unavailable", "Mock Exam benefit/admission policy awaits approval.");
    public Task AuthorizeReview(Guid owner, Guid attempt, CancellationToken ct) =>
        Task.CompletedTask; // Service checks ownership and terminal status first.
}

// Phase 4 reuses the existing simulator access decision; it creates no new grant,
// product or unrestricted start path. Content/publication gates still apply before
// admission, and terminal owner review survives later subscription expiry.
public sealed class ExistingCentMockAdmission(CentAccess access) : ICentMockAdmission
{
    public bool IsConfigured => true;
    public async Task<string> AuthorizeStart(Guid owner, ExamDefinition definition, CancellationToken ct)
    {
        CentModule.Check(await access.HasSubscription(owner, ct), "دسترسی فعلی آزمون‌های شبیه‌ساز برای شروع لازم است.", 403, "exam_access_required");
        return "existing-simulator-access-v1";
    }
    public Task AuthorizeReview(Guid owner, Guid attempt, CancellationToken ct) => Task.CompletedTask;
}
public sealed record StartMockExam(Guid RequestId, string ExamVersionId, string ConfigurationRevision);
public sealed record StartedMockExam(bool Created, object State);

public sealed class CentExamService(ICentExamStore store, ICentMockAdmission admission)
{
    public async Task<object> Versions(CancellationToken ct)
    {
        var rows = await store.Versions(ct);
        var versions = new List<object>();
        foreach (var v in rows.Where(v => v.Status != "DRAFT")) {
            var d = CentExamJson.Read<ExamDefinition>(v.DefinitionJson);
            var reason = await Unavailable(v, d, ct);
            versions.Add(new { examVersionId = v.Id, label = d.Label, d.TotalQuestions, d.TotalDurationSeconds,
                status = v.Status, availableForNewStarts = reason is null, availabilityReason = reason });
        }
        return new { contractVersion = 1, versions };
    }
    public async Task<object> Configuration(string versionId, CancellationToken ct)
    {
        var v = await Version(versionId, ct); var d = CentExamJson.Read<ExamDefinition>(v.DefinitionJson);
        var reason = await Unavailable(v, d, ct);
        return new { contractVersion = 1, examVersionId = v.Id, v.ConfigurationRevision, d.Label,
            administrationCycle = new { startDate = d.CycleStartDate, endDateInclusive = d.CycleEndDateInclusive },
            academicYear = (string?)null, d.Sections, d.TotalQuestions, d.TotalDurationSeconds, optionCount = ExamDefinition.OptionCount,
            scoring = new { policyId = d.Scoring.PolicyId, correct = CentExamScoring.Points(d.Scoring.CorrectQuarterPoints),
                incorrect = CentExamScoring.Points(d.Scoring.IncorrectQuarterPoints), unanswered = CentExamScoring.Points(d.Scoring.UnansweredQuarterPoints) },
            d.TimingPolicyId, d.SubmissionPolicyId, d.SyllabusRevisionId, d.ContentReleaseId,
            availableForNewStarts = reason is null, availabilityReason = reason };
    }
    private async Task<CentExamVersion> Version(string id, CancellationToken ct)
    {
        var row = (await store.Versions(ct)).SingleOrDefault(v => v.Id == id && v.Status != "DRAFT");
        CentModule.Check(row is not null, "Exam version not found.", 404, "exam_unavailable"); return row!;
    }
    private async Task<string?> Unavailable(CentExamVersion v, ExamDefinition d, CancellationToken ct)
    {
        if (v.Status != "PUBLISHED" || !v.AvailableForNewStarts) return "release_not_activated";
        if (!admission.IsConfigured || d.SubmissionPolicyId is null) return "policies_pending";
        if (d.ContentReleaseId is null || d.SyllabusRevisionId is null) return "content_pending";
        var release = await store.Release(d.ContentReleaseId, ct);
        if (release?.ReviewedAt is null || release.ReviewedBy is null || release.ExamVersionId != d.ExamVersionId ||
            release.SyllabusRevisionId != d.SyllabusRevisionId) return "content_pending";
        return null;
    }
    public Task<StartedMockExam> Start(Guid owner, StartMockExam input, CancellationToken ct)
    {
        CentModule.Check(input.RequestId != Guid.Empty && !string.IsNullOrWhiteSpace(input.ExamVersionId) &&
            !string.IsNullOrWhiteSpace(input.ConfigurationRevision), "Request, version and configuration are required.");
        return store.Start(owner, input.RequestId, async (existing, now) => {
            if (existing is not null) {
                CentModule.Check(existing.Mock.ExamVersionId == input.ExamVersionId &&
                    existing.Mock.ConfigurationRevision == input.ConfigurationRevision,
                    "Start request was used with another configuration.", 409, "command_conflict");
                CentExamEngine.Reconcile(existing, now);
                return (existing, new StartedMockExam(false, State(existing, now)));
            }
            var row = await Version(input.ExamVersionId, ct);
            CentModule.Check(row.ConfigurationRevision == input.ConfigurationRevision,
                "Configuration changed; select a current configuration.", 409, "configuration_changed");
            var d = CentExamJson.Read<ExamDefinition>(row.DefinitionJson);
            CentModule.Check(row.Id == d.ExamVersionId && row.ConfigurationRevision == d.ConfigurationRevision &&
                row.SyllabusRevisionId == d.SyllabusRevisionId && row.ContentReleaseId == d.ContentReleaseId,
                "Definition metadata does not match its immutable payload.", 409, "exam_unavailable");
            CentModule.Check(await Unavailable(row, d, ct) is null, "Exam policies/content are not activated.", 409, "exam_unavailable");
            var policyId = await admission.AuthorizeStart(owner, d, ct);
            var release = await store.Release(d.ContentReleaseId!, ct);
            var run = CentExamAssembly.Assemble(owner, input.RequestId, d,
                CentExamJson.Read<ExamContentManifest>(release!.ManifestJson), policyId, now);
            return (run, new StartedMockExam(true, State(run, now)));
        }, ct);
    }
    public Task<object> Read(Guid owner, Guid id, bool content, CancellationToken ct) => store.Execute(owner, id, (run, now) => {
        CentExamEngine.Reconcile(run, now);
        return content ? ActiveContent(run, now) : State(run, now);
    }, ct);
    public Task<object> Command(Guid owner, Guid id, string action, ExamCommand input, CancellationToken ct) =>
        store.Execute(owner, id, (run, now) => {
            ExamReceipt receipt;
            try { receipt = CentExamEngine.Apply(run, action, input, now); }
            catch (CentException e) { e.Data["currentState"] = State(run, now); throw; }
            return (object)new { receipt, currentState = State(run, now),
                resultPath = run.Mock.Status is "COMPLETED" or "EXPIRED" ? $"/api/cent/v1/assessment/mock-attempts/{id}/result" : null };
        }, ct);
    public Task<object> Result(Guid owner, Guid id, CancellationToken ct) => store.Execute(owner, id, (run, now) => {
        CentExamEngine.Reconcile(run, now);
        CentModule.Check(run.Mock.ResultJson is not null, "The attempt is not finalized.", 409, "attempt_not_finalized");
        var result = CentExamJson.Read<ExamResult>(run.Mock.ResultJson!);
        // Flatten counts according to A6; ResultJson is retained verbatim and never recomputed.
        var json = JsonSerializer.SerializeToNode(result, CentExamJson.Options)!.AsObject();
        json.Remove("totals");
        foreach (var kv in JsonSerializer.SerializeToNode(result.Totals, CentExamJson.Options)!.AsObject()) json[kv.Key] = kv.Value?.DeepClone();
        json["comparability"] = JsonSerializer.SerializeToNode(new { signatureId = (string?)null,
            eligibleForCompletedTrend = false, caveat = "Uncalibrated Itatest practice forms; no official equivalence. Comparison cohort is not approved." }, CentExamJson.Options);
        if (result.ResultBasis is null) json["scoreUnavailableReason"] = "no_score_policy";
        return (object)json;
    }, ct);
    public async Task<object> Review(Guid owner, Guid id, CancellationToken ct)
    {
        // Lookup/terminal guard precedes policy authorization: foreign resources always 404.
        await store.Execute(owner, id, (run, now) => {
            CentExamEngine.Reconcile(run, now);
            CentModule.Check(run.Mock.ResultJson is not null, "The attempt is not finalized.", 409, "attempt_not_finalized");
            return true;
        }, ct);
        return await AuthorizedReview(owner, id, ct);
    }
    private async Task<object> AuthorizedReview(Guid owner, Guid id, CancellationToken ct)
    {
        await admission.AuthorizeReview(owner, id, ct);
        return await store.Execute(owner, id, (run, now) => {
            var rules = run.Rules;
            return (object)new { attemptId = id, run.Mock.Status, passages = rules.Passages, groups = rules.Groups,
                questions = run.Questions.Select(row => {
                    var q = CentExamJson.Read<AssignedExamQuestion>(row.SnapshotJson);
                    return new { questionId = q.Question.QuestionId, sectionId = q.SectionId, position = row.Position + 1,
                        sectionSubjectId = q.Question.SubjectId, q.Question.Stem, q.Question.Options, q.Question.Media,
                        q.Question.PassageRevisionId, q.Question.GroupRevisionId,
                        selectedOptionId = row.Answer is { } answer ? q.Question.Options[answer].Id : null,
                        q.Question.CorrectOptionId, q.Question.Explanation };
                }).ToArray(), nextCursor = (string?)null };
        }, ct);
    }
    public async Task<object> History(Guid owner, Guid? cursor, int limit, CancellationToken ct)
    {
        CentModule.Check(limit is > 0 and <= 100, "Limit must be between 1 and 100.");
        var ids = await store.History(owner, cursor, limit, ct);
        var items = new List<object>();
        foreach (var id in ids) items.Add(await Read(owner, id, false, ct));
        return new { items, nextCursor = ids.Length == limit ? (Guid?)ids[^1] : null };
    }
    public static object State(CentExamRun run, DateTimeOffset now)
    {
        var rules = run.Rules;
        object Section(CentAttemptSection s) => new { sectionId = s.Id, s.SectionKey, s.Order, s.SubjectId,
            s.QuestionCount, s.DurationSeconds, s.State, s.ActivatedAt, s.Deadline, s.ClosedAt, s.ProcessedAt };
        return new { contractVersion = 1, attemptId = run.Attempt.Id, run.Mock.ExamVersionId, run.Mock.ConfigurationRevision,
            rules.RulesSnapshotId, rules.Definition.ContentReleaseId, run.Mock.Status, run.Mock.Revision, serverNow = now,
            run.Mock.ActiveSectionId, activeSection = run.Sections.Where(s => s.Id == run.Mock.ActiveSectionId).Select(Section).SingleOrDefault(),
            sections = run.Sections.OrderBy(s => s.Order).Select(Section).ToArray(), run.Mock.EffectiveEndedAt,
            run.Mock.FinalizationReason, officialSessionId = (string?)null, macroPeriodId = (string?)null };
    }
    public static object ActiveContent(CentExamRun run, DateTimeOffset now)
    {
        var rules = run.Rules;
        var rows = run.Questions.Select(r => (Row: r, Q: CentExamJson.Read<AssignedExamQuestion>(r.SnapshotJson)))
            .Where(x => x.Q.SectionId == run.Mock.ActiveSectionId).ToArray();
        var groupIds = rows.Select(x => x.Q.Question.GroupRevisionId).Where(x => x is not null).ToHashSet();
        var groups = rules.Groups.Where(g => groupIds.Contains(g.GroupRevisionId)).ToArray();
        return new { attemptId = run.Attempt.Id, run.Mock.Revision, serverNow = now, sectionId = run.Mock.ActiveSectionId,
            activeSection = run.Mock.ActiveSectionId is null ? null : State(run, now), deadline = run.Mock.ActiveDeadline,
            passages = rules.Passages.Where(p => groups.Any(g => g.PassageRevisionId == p.PassageRevisionId))
                .Select(p => new { passageSnapshotId = p.PassageRevisionId, p.PassageRevisionId, p.Text, p.Media }).ToArray(),
            groups = groups.Select(g => new { g.GroupRevisionId, passageSnapshotId = g.PassageRevisionId, declaredQuestionCount = g.QuestionRevisionIds.Length }).ToArray(),
            questions = rows.Select(x => new { questionId = x.Q.Question.QuestionId, x.Q.Question.QuestionRevisionId,
                position = x.Q.SectionPosition, x.Q.Question.GroupRevisionId, passageSnapshotId = x.Q.Question.PassageRevisionId,
                x.Q.Question.Stem, x.Q.Question.Options, x.Q.Question.Media,
                selectedOptionId = x.Row.Answer is { } a ? x.Q.Question.Options[a].Id : null }).ToArray() };
    }
}
