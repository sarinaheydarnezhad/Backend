using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Cent.Module.Exams;

public interface ICentExamStore
{
    Task<CentExamVersion[]> Versions(CancellationToken ct);
    Task<CentExamContentRelease?> Release(string id, CancellationToken ct);
    Task<T> Start<T>(Guid owner, Guid requestId, Func<CentExamRun?, DateTimeOffset, Task<(CentExamRun Run, T Value)>> operation, CancellationToken ct);
    Task<T> Execute<T>(Guid owner, Guid id, Func<CentExamRun, DateTimeOffset, T> operation, CancellationToken ct);
    Task<Guid[]> History(Guid owner, Guid? before, int limit, CancellationToken ct);
    Task<Guid[]> Due(DateTimeOffset now, CancellationToken ct);
    Task<Guid?> Owner(Guid attempt, CancellationToken ct);
}

// SQL Server ownership was established from CentDbContext and its migration history.
// A different provider needs an independently tested locking implementation.
public sealed class CentExamStore(CentDbContext db, TimeProvider clock) : ICentExamStore
{
    public Task<CentExamVersion[]> Versions(CancellationToken ct) => db.ExamVersions.AsNoTracking().OrderBy(v => v.Id).ToArrayAsync(ct);
    public Task<CentExamContentRelease?> Release(string id, CancellationToken ct) => db.ExamContentReleases.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
    private void RequireProvider() => CentModule.Check(db.Database.IsSqlServer(),
        "Versioned Mock Exams require the reviewed SQL Server locking implementation.", 409, "unsupported_exam_contract");
    public async Task<T> Start<T>(Guid owner, Guid requestId,
        Func<CentExamRun?, DateTimeOffset, Task<(CentExamRun Run, T Value)>> operation, CancellationToken ct)
    {
        RequireProvider();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // Per-owner DB lock also serializes competing first starts with the same requestId.
        var user = await db.Users.FromSqlInterpolated($"SELECT * FROM cent_users WITH (UPDLOCK, ROWLOCK) WHERE Id = {owner}")
            .SingleOrDefaultAsync(ct);
        CentModule.Check(user is not null, "Owner not found.", 404, "attempt_not_found");
        var old = await db.Attempts.SingleOrDefaultAsync(a => a.UserId == owner && a.RequestId == requestId, ct);
        CentModule.Check(old is null || old.Kind == "mock", "Request identity belongs to an existing assessment.", 409, "command_conflict");
        var run = old is null ? null : await Load(owner, old.Id, ct);
        var (updated, value) = await operation(run, clock.GetUtcNow());
        if (run is null) {
            db.Attempts.Add(updated.Attempt); db.MockAttempts.Add(updated.Mock);
            db.AttemptSections.AddRange(updated.Sections); db.AttemptQuestions.AddRange(updated.Questions);
        }
        AddReceipts(updated);
        await Save(ct); await tx.CommitAsync(ct);
        return value;
    }
    public async Task<T> Execute<T>(Guid owner, Guid id, Func<CentExamRun, DateTimeOffset, T> operation, CancellationToken ct)
    {
        RequireProvider();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var run = await Load(owner, id, ct);
        // Decision time is read only after the database lock is owned.
        var now = clock.GetUtcNow();
        T? value = default; CentException? error = null;
        try { value = operation(run, now); }
        catch (CentException ex) { error = ex; }
        AddReceipts(run);
        await Save(ct);
        await tx.CommitAsync(ct);
        if (error is not null) throw error;
        return value!;
    }
    private async Task Save(CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        // Close the prior ACTIVE row before setting its successor ACTIVE. This avoids
        // relying on EF update ordering for the filtered single-active-section index.
        var activations = db.ChangeTracker.Entries<CentAttemptSection>().Where(e => e.State == EntityState.Modified &&
            e.Entity.State == "ACTIVE" && e.OriginalValues.GetValue<string>(nameof(CentAttemptSection.State)) != "ACTIVE").ToArray();
        foreach (var e in activations) e.State = EntityState.Unchanged;
        await db.SaveChangesAsync(ct);
        foreach (var e in activations) e.State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }
    private void AddReceipts(CentExamRun run)
    {
        foreach (var r in run.Receipts)
            if (db.Entry(r).State == EntityState.Detached) db.ExamCommandReceipts.Add(r);
    }
    private async Task<CentExamRun> Load(Guid owner, Guid id, CancellationToken ct)
    {
        // Owner predicate is in the lock query: foreign/missing IDs are indistinguishable.
        var mock = await db.MockAttempts.FromSqlInterpolated($"SELECT m.* FROM cent_mock_attempts m WITH (UPDLOCK, ROWLOCK) INNER JOIN cent_attempts a ON a.Id = m.AttemptId WHERE m.AttemptId = {id} AND a.UserId = {owner}")
            .SingleOrDefaultAsync(ct);
        CentModule.Check(mock is not null, "Attempt not found.", 404, "attempt_not_found");
        return new CentExamRun { Mock = mock!, Attempt = await db.Attempts.SingleAsync(a => a.Id == id, ct),
            Sections = await db.AttemptSections.Where(s => s.AttemptId == id).OrderBy(s => s.Order).ToListAsync(ct),
            Questions = await db.AttemptQuestions.Where(q => q.AttemptId == id).OrderBy(q => q.Position).ToListAsync(ct),
            Receipts = await db.ExamCommandReceipts.Where(r => r.AttemptId == id).ToListAsync(ct) };
    }
    public async Task<Guid[]> History(Guid owner, Guid? before, int limit, CancellationToken ct)
    {
        var query = from a in db.Attempts.AsNoTracking() join m in db.MockAttempts.AsNoTracking() on a.Id equals m.AttemptId
            where a.UserId == owner select a;
        if (before is { } id) {
            var cursor = await query.SingleOrDefaultAsync(a => a.Id == id, ct);
            CentModule.Check(cursor is not null, "Invalid history cursor.");
            query = query.Where(a => a.CreatedAt < cursor!.CreatedAt || (a.CreatedAt == cursor.CreatedAt && a.Id.CompareTo(id) < 0));
        }
        return await query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(limit).Select(a => a.Id).ToArrayAsync(ct);
    }
    public Task<Guid[]> Due(DateTimeOffset now, CancellationToken ct) => db.MockAttempts.AsNoTracking()
        .Where(m => (m.Status == "ACTIVE" && m.ActiveDeadline <= now) || m.Status == "SUBMITTING")
        .OrderBy(m => m.ActiveDeadline).Select(m => m.AttemptId).Take(100).ToArrayAsync(ct);
    public Task<Guid?> Owner(Guid attempt, CancellationToken ct) => db.Attempts.AsNoTracking().Where(a => a.Id == attempt && a.Kind == "mock")
        .Select(a => (Guid?)a.UserId).SingleOrDefaultAsync(ct);
}
