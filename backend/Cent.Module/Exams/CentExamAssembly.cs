namespace Cent.Module.Exams;

public sealed record ExamOption(string Id, string Text);
public sealed record ExamPassage(string PassageRevisionId, string Text, string? Media = null);
public sealed record ExamGroup(string GroupRevisionId, string SubjectId, string PassageRevisionId, string[] QuestionRevisionIds);
public sealed record ExamQuestion(Guid QuestionId, string QuestionRevisionId, string SubjectId, Guid TopicId,
    string Difficulty, string Stem, ExamOption[] Options, string CorrectOptionId, string Explanation,
    string? GroupRevisionId = null, string? PassageRevisionId = null, string? Media = null);
public sealed record ExamEligibility(string QuestionRevisionId, string SectionKey, string Status);
public sealed record ExamContentManifest(string ExamVersionId, string SyllabusRevisionId,
    ExamQuestion[] Questions, ExamPassage[] Passages, ExamGroup[] Groups, ExamEligibility[] Eligibility);
public sealed record AssignedExamQuestion(Guid SectionId, int SectionPosition, ExamQuestion Question);
public sealed record ExamRulesSnapshot(ExamDefinition Definition, string RulesSnapshotId,
    ExamPassage[] Passages, ExamGroup[] Groups, string AdmissionPolicyId);

public sealed class CentExamRun
{
    public CentAttempt Attempt { get; set; } = new();
    public CentMockAttempt Mock { get; set; } = new();
    public List<CentAttemptSection> Sections { get; set; } = [];
    public List<CentAttemptQuestion> Questions { get; set; } = [];
    public List<CentExamCommandReceipt> Receipts { get; set; } = [];
    public ExamRulesSnapshot Rules => CentExamJson.Read<ExamRulesSnapshot>(Mock.RulesSnapshotJson);
}

public static class CentExamAssembly
{
    // An exact subset of atomic groups is selected. Greedy selection could report a false shortfall.
    public static CentExamRun Assemble(Guid owner, Guid requestId, ExamDefinition definition,
        ExamContentManifest manifest, string admissionPolicyId, DateTimeOffset now)
    {
        definition.Validate();
        Ready(definition.SubmissionPolicyId is not null && definition.SyllabusRevisionId is not null &&
            definition.ContentReleaseId is not null && !string.IsNullOrWhiteSpace(admissionPolicyId));
        Ready(manifest.ExamVersionId == definition.ExamVersionId && manifest.SyllabusRevisionId == definition.SyllabusRevisionId);
        Ready(manifest.Questions.Select(q => q.QuestionRevisionId).Distinct().Count() == manifest.Questions.Length &&
            manifest.Questions.Select(q => q.QuestionId).Distinct().Count() == manifest.Questions.Length &&
            manifest.Groups.Select(g => g.GroupRevisionId).Distinct().Count() == manifest.Groups.Length &&
            manifest.Passages.Select(p => p.PassageRevisionId).Distinct().Count() == manifest.Passages.Length);
        var questions = manifest.Questions.ToDictionary(q => q.QuestionRevisionId, StringComparer.Ordinal);
        var groups = manifest.Groups.ToDictionary(g => g.GroupRevisionId, StringComparer.Ordinal);
        var passages = manifest.Passages.ToDictionary(p => p.PassageRevisionId, StringComparer.Ordinal);
        foreach (var q in manifest.Questions) {
            Ready(q.QuestionId != Guid.Empty && q.TopicId != Guid.Empty && !string.IsNullOrWhiteSpace(q.QuestionRevisionId) &&
                !string.IsNullOrWhiteSpace(q.Stem) && !string.IsNullOrWhiteSpace(q.Explanation) &&
                q.Options is { Length: ExamDefinition.OptionCount } &&
                q.Options.All(o => !string.IsNullOrWhiteSpace(o.Id) && !string.IsNullOrWhiteSpace(o.Text)) &&
                q.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() == ExamDefinition.OptionCount &&
                q.Options.Select(o => o.Text.Trim()).Distinct(StringComparer.Ordinal).Count() == ExamDefinition.OptionCount &&
                q.Options.Count(o => o.Id == q.CorrectOptionId) == 1);
            Ready((q.GroupRevisionId is null) == (q.PassageRevisionId is null));
            if (q.GroupRevisionId is { } gid) {
                Ready(groups.TryGetValue(gid, out var g) && g.SubjectId == q.SubjectId &&
                    g.PassageRevisionId == q.PassageRevisionId && g.QuestionRevisionIds.Contains(q.QuestionRevisionId));
            }
        }
        foreach (var group in manifest.Groups) {
            Ready(group.QuestionRevisionIds.Length > 0 && group.QuestionRevisionIds.Distinct().Count() == group.QuestionRevisionIds.Length &&
                passages.TryGetValue(group.PassageRevisionId, out var p) && !string.IsNullOrWhiteSpace(p.Text) &&
                group.QuestionRevisionIds.All(id => questions.TryGetValue(id, out var q) && q.GroupRevisionId == group.GroupRevisionId &&
                    q.SubjectId == group.SubjectId && q.PassageRevisionId == group.PassageRevisionId));
        }
        var run = new CentExamRun();
        run.Attempt = new CentAttempt { UserId = owner, RequestId = requestId, Kind = "mock",
            AssessmentMode = "versioned-mock-v1", CreatedAt = now, DurationMinutes = definition.TotalDurationSeconds / 60,
            CourseCodesJson = CentExamJson.Write(definition.Sections.Select(s => s.SubjectId)) };
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var usedGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in definition.Sections) {
            var section = new CentAttemptSection { AttemptId = run.Attempt.Id, SectionKey = s.SectionKey,
                SubjectId = s.SubjectId, Order = s.Order, QuestionCount = s.QuestionCount, DurationSeconds = s.DurationSeconds };
            run.Sections.Add(section);
            bool Eligible(ExamQuestion q) => q.SubjectId == s.SubjectId && !selected.Contains(q.QuestionRevisionId) &&
                manifest.Eligibility.Any(e => e.QuestionRevisionId == q.QuestionRevisionId && e.SectionKey == s.SectionKey && e.Status == "eligible");
            var units = manifest.Groups.Where(g => g.SubjectId == s.SubjectId && g.QuestionRevisionIds.All(id => Eligible(questions[id])))
                .Select(g => g.QuestionRevisionIds.Select(id => questions[id]).ToArray())
                .Concat(manifest.Questions.Where(q => q.GroupRevisionId is null && Eligible(q)).Select(q => new[] { q }))
                .OrderBy(_ => Random.Shared.Next()).ToArray();
            var solutions = new Dictionary<int, ExamQuestion[]> { [0] = [] };
            foreach (var unit in units)
                foreach (var n in solutions.Keys.OrderDescending().ToArray())
                    if (n + unit.Length <= s.QuestionCount && !solutions.ContainsKey(n + unit.Length))
                        solutions[n + unit.Length] = [.. solutions[n], .. unit];
            Ready(solutions.TryGetValue(s.QuestionCount, out var chosen));
            foreach (var (q, i) in chosen!.Select((q, i) => (q, i))) {
                Ready(selected.Add(q.QuestionRevisionId));
                if (q.GroupRevisionId is { } gid) usedGroups.Add(gid);
                run.Questions.Add(new CentAttemptQuestion { AttemptId = run.Attempt.Id, Position = run.Questions.Count,
                    QuestionId = q.QuestionId, SnapshotJson = CentExamJson.Write(new AssignedExamQuestion(section.Id, i + 1, q)) });
            }
        }
        var pickedGroups = manifest.Groups.Where(g => usedGroups.Contains(g.GroupRevisionId)).ToArray();
        var pickedPassages = manifest.Passages.Where(p => pickedGroups.Any(g => g.PassageRevisionId == p.PassageRevisionId)).ToArray();
        run.Mock = new CentMockAttempt { AttemptId = run.Attempt.Id, ExamVersionId = definition.ExamVersionId,
            ConfigurationRevision = definition.ConfigurationRevision,
            RulesSnapshotJson = CentExamJson.Write(new ExamRulesSnapshot(definition, Guid.NewGuid().ToString(), pickedPassages, pickedGroups, admissionPolicyId)) };
        CentExamEngine.Activate(run, run.Sections[0], now);
        return run;
    }
    public static void Ready(bool condition) => CentModule.Check(condition,
        "A complete reviewed exam is not available.", 409, "exam_not_ready");
}
