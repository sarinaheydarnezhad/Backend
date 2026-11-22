using Cent.Module;
using Cent.Module.Exams;

namespace Cent.Exam.Tests;

public sealed class ExamClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
public sealed class TestAdmission : ICentMockAdmission
{
    public bool IsConfigured { get; set; } = true;
    public Task<string> AuthorizeStart(Guid owner, ExamDefinition definition, CancellationToken ct) => Task.FromResult("test-approved-admission");
    public Task AuthorizeReview(Guid owner, Guid attempt, CancellationToken ct) => Task.CompletedTask;
}
public sealed class ExamFixture
{
    public Guid Owner { get; } = Guid.NewGuid();
    public ExamClock Clock { get; } = new();
    public TestAdmission Admission { get; } = new();
    public MemoryExamStore Store { get; }
    public CentExamService Service { get; }
    public ExamDefinition Definition { get; }
    public ExamContentManifest Manifest { get; }
    public ExamFixture(bool historical = false, string profile = "expired-feedback-v1", bool groupReading = true)
    {
        Definition = (historical ? CentExamBlueprints.Historical : CentExamBlueprints.Current) with {
            ConfigurationRevision = "test-config-1", SyllabusRevisionId = "test-syllabus-1", ContentReleaseId = "test-content-1",
            SubmissionPolicyId = profile };
        var questions = Definition.Sections.SelectMany(s => Enumerable.Range(1, s.QuestionCount).Select(i =>
            new ExamQuestion(Guid.NewGuid(), $"{s.SubjectId}-q{i}-r1", s.SubjectId, Guid.NewGuid(), "medium",
                $"Synthetic {s.SubjectId} item {i}", [new("a", "Option A"), new("b", "Option B"), new("c", "Option C"), new("d", "Option D"), new("e", "Option E")],
                "b", "Synthetic reviewed explanation.", s.SubjectId == "reading" && groupReading ? $"reading-group-{(i - 1) / 5}" : null,
                s.SubjectId == "reading" && groupReading ? "reading-passage-r1" : null))).ToArray();
        var groups = questions.Where(q => q.GroupRevisionId is not null).GroupBy(q => q.GroupRevisionId!).Select(g =>
            new ExamGroup(g.Key, "reading", "reading-passage-r1", g.Select(q => q.QuestionRevisionId).ToArray())).ToArray();
        Manifest = new(Definition.ExamVersionId, Definition.SyllabusRevisionId!, questions,
            groups.Length == 0 ? [] : [new("reading-passage-r1", "An original synthetic passage for infrastructure tests only.")], groups,
            questions.Select(q => new ExamEligibility(q.QuestionRevisionId, q.SubjectId, "eligible")).ToArray());
        Store = new(Clock, Definition, Manifest); Service = new(Store, Admission);
    }
    public CentExamRun Run() => CentExamAssembly.Assemble(Owner, Guid.NewGuid(), Definition, Manifest, "test-admission", Clock.Now);
    public async Task<Guid> Start(Guid? requestId = null)
    {
        var started = await Service.Start(Owner, new(requestId ?? Guid.NewGuid(), Definition.ExamVersionId, Definition.ConfigurationRevision), default);
        return System.Text.Json.JsonSerializer.SerializeToElement(started.State, CentExamJson.Options).GetProperty("attemptId").GetGuid();
    }
}

// Transactional test double, never a database. Clone/commit semantics model lost writes
// and retries; SQL Server lock/transaction integration requires a separately approved DB.
public sealed class MemoryExamStore : ICentExamStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ExamClock clock;
    public Dictionary<Guid, CentExamRun> Runs { get; } = [];
    public CentExamVersion Version { get; }
    public CentExamContentRelease Content { get; }
    public bool FailNextCommit { get; set; }
    public MemoryExamStore(ExamClock clock, ExamDefinition d, ExamContentManifest m)
    {
        this.clock = clock;
        Version = new() { Id = d.ExamVersionId, ConfigurationRevision = d.ConfigurationRevision,
            DefinitionJson = CentExamJson.Write(d), Status = "PUBLISHED", AvailableForNewStarts = true,
            SyllabusRevisionId = d.SyllabusRevisionId, ContentReleaseId = d.ContentReleaseId };
        Content = new() { Id = d.ContentReleaseId!, ExamVersionId = d.ExamVersionId, SyllabusRevisionId = d.SyllabusRevisionId!,
            ManifestJson = CentExamJson.Write(m), ReviewedAt = clock.Now, ReviewedBy = Guid.NewGuid() };
    }
    public Task<CentExamVersion[]> Versions(CancellationToken ct) => Task.FromResult(new[] { Version });
    public Task<CentExamContentRelease?> Release(string id, CancellationToken ct) => Task.FromResult<CentExamContentRelease?>(id == Content.Id ? Content : null);
    public async Task<T> Start<T>(Guid owner, Guid requestId, Func<CentExamRun?, DateTimeOffset, Task<(CentExamRun Run, T Value)>> operation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            var old = Runs.Values.SingleOrDefault(r => r.Attempt.UserId == owner && r.Attempt.RequestId == requestId);
            var (run, value) = await operation(old is null ? null : Clone(old), clock.Now);
            Commit(run); return value;
        } finally { gate.Release(); }
    }
    public async Task<T> Execute<T>(Guid owner, Guid id, Func<CentExamRun, DateTimeOffset, T> operation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            if (!Runs.TryGetValue(id, out var saved) || saved.Attempt.UserId != owner)
                throw new CentException(404, "attempt_not_found", "Attempt not found.");
            var run = Clone(saved);
            try { var value = operation(run, clock.Now); Commit(run); return value; }
            catch (CentException) { Commit(run); throw; }
        } finally { gate.Release(); }
    }
    private void Commit(CentExamRun run)
    {
        if (FailNextCommit) { FailNextCommit = false; throw new IOException("Synthetic durable storage failure."); }
        Runs[run.Attempt.Id] = Clone(run);
    }
    public static CentExamRun Clone(CentExamRun r) => CentExamJson.Read<CentExamRun>(CentExamJson.Write(r));
    public Task<Guid[]> History(Guid owner, Guid? before, int limit, CancellationToken ct) => Task.FromResult(Runs.Values
        .Where(r => r.Attempt.UserId == owner).OrderByDescending(r => r.Attempt.CreatedAt).Select(r => r.Attempt.Id).Take(limit).ToArray());
    public Task<Guid[]> Due(DateTimeOffset now, CancellationToken ct) => Task.FromResult(Runs.Values.Where(r => r.Mock.Status == "ACTIVE" && r.Mock.ActiveDeadline <= now).Select(r => r.Attempt.Id).ToArray());
    public Task<Guid?> Owner(Guid attempt, CancellationToken ct) => Task.FromResult(Runs.TryGetValue(attempt, out var r) ? (Guid?)r.Attempt.UserId : null);
}
