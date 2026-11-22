using System.Globalization;
using System.Text.Json;

namespace Cent.Module.Exams;

public static class CentExamJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new InvalidOperationException("Missing exam snapshot.");
}

public sealed record ExamSectionDefinition(string SectionKey, int Order, string SubjectId, int QuestionCount, int DurationSeconds);
public sealed record ExamScoringRules(string PolicyId = "raw-quarter-point-v1", int CorrectQuarterPoints = 4,
    int IncorrectQuarterPoints = -1, int UnansweredQuarterPoints = 0);
public sealed record ExamDefinition(string ExamVersionId, string Label, string ConfigurationRevision,
    DateOnly CycleStartDate, DateOnly CycleEndDateInclusive, ExamSectionDefinition[] Sections,
    ExamScoringRules Scoring, string? SyllabusRevisionId = null, string? ContentReleaseId = null,
    string TimingPolicyId = "continuous-v1", string? SubmissionPolicyId = null)
{
    public int TotalQuestions => Sections.Sum(s => s.QuestionCount);
    public int TotalDurationSeconds => Sections.Sum(s => s.DurationSeconds);
    public const int OptionCount = 5;
    public void Validate()
    {
        CentModule.Check(!string.IsNullOrWhiteSpace(ExamVersionId) && ExamVersionId.Length <= 100 &&
            !string.IsNullOrWhiteSpace(ConfigurationRevision) && ConfigurationRevision.Length <= 100 &&
            !string.IsNullOrWhiteSpace(Label) && CycleEndDateInclusive >= CycleStartDate,
            "Invalid exam identity.");
        CentModule.Check(Sections is { Length: > 0 and <= 20 } && Sections.All(s => s is not null) &&
            Sections.Select(s => s.Order).SequenceEqual(Enumerable.Range(1, Sections.Length)) &&
            Sections.Select(s => s.SectionKey).Distinct(StringComparer.Ordinal).Count() == Sections.Length &&
            Sections.Select(s => s.SubjectId).Distinct(StringComparer.Ordinal).Count() == Sections.Length &&
            Sections.All(s => !string.IsNullOrWhiteSpace(s.SectionKey) && s.SectionKey.Length <= 24 &&
                !string.IsNullOrWhiteSpace(s.SubjectId) && s.SubjectId.Length <= 24 &&
                s.QuestionCount is > 0 and <= 100 && s.DurationSeconds is > 0 and <= 14400),
            "Invalid ordered exam sections.");
        CentModule.Check(Scoring == new ExamScoringRules() && TimingPolicyId == "continuous-v1" &&
            SubmissionPolicyId is null or "expired-feedback-v1" or "expired-counts-only-v1",
            "Unsupported exam policy.", 409, "unsupported_exam_contract");
    }
}

// Blueprint identity is independent of syllabus/content publication or local activation.
// No historical syllabus mapping or eligible question bank is inferred here.
public static class CentExamBlueprints
{
    public static ExamDefinition Historical => new("cent-s-2025-2026-r1", "CEnT-S 2025–2026", "blueprint-2025-2026-r1",
        new(2025, 11, 1), new(2026, 10, 31), Sections(false), new());
    public static ExamDefinition Current => new("cent-s-2026-2027-r1", "CEnT-S 2026–2027", "blueprint-2026-2027-r1",
        new(2026, 11, 1), new(2027, 10, 31), Sections(true), new());
    private static ExamSectionDefinition[] Sections(bool reading)
    {
        var rows = new List<(string Subject, int Questions, int Seconds)>();
        if (reading) rows.Add(("reading", 10, 1800));
        rows.AddRange([("math", 15, 1800), ("logic", 15, 1800), ("biology", 10, 1200),
            ("chemistry", 10, 1200), ("physics", 5, 600)]);
        return rows.Select((r, i) => new ExamSectionDefinition(r.Subject, i + 1, r.Subject, r.Questions, r.Seconds)).ToArray();
    }
}

public sealed record ExamRatio(int Numerator, int Denominator, string? DisplayPercent);
public sealed record ExamCounts(int QuestionCount, int CorrectCount, int IncorrectCount, int UnansweredCount,
    int AnsweredCount, int? RawQuarterPoints, string? RawScore, string MaximumRawScore, string MinimumRawScore,
    ExamRatio Accuracy, ExamRatio CompletionRate);
public static class CentExamScoring
{
    public static ExamCounts Calculate(int total, int correct, int incorrect, bool includeScore = true)
    {
        if (total < 0 || correct < 0 || incorrect < 0 || (long)correct + incorrect > total)
            throw new ArgumentOutOfRangeException(nameof(total));
        var answered = checked(correct + incorrect);
        var quarters = checked(4 * correct - incorrect);
        return new(total, correct, incorrect, total - answered, answered, includeScore ? quarters : null,
            includeScore ? Points(quarters) : null, Points(checked(4 * total)), Points(-total),
            Ratio(correct, answered), Ratio(answered, total));
    }
    public static string Points(int quarters) => (quarters / 4m).ToString("0.00", CultureInfo.InvariantCulture);
    private static ExamRatio Ratio(int n, int d) => new(n, d, d == 0 ? null :
        decimal.Round(n * 100m / d, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture));
}
