using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Cent.Module;

public sealed record TopicMastery(Guid Id, Guid ChapterId, string Course, string Title, int AvailableQuestions,
    int EvidenceQuestions, int Correct, int? AccuracyPercentage, int? MasteryPercentage, string Status);

public sealed class CentLearning(CentDbContext db)
{
    // The latest answer to each distinct question counts once. Repetition cannot increase evidence.
    // Submitted practice and assessment answers both contribute to provisional progress.
    public async Task<object> Mastery(Guid userId, CancellationToken ct)
    {
        var topics = await db.Topics.AsNoTracking().OrderBy(t => t.SortOrder).ToListAsync(ct);
        var bank = await db.Questions.AsNoTracking().Where(q => q.Active && q.TopicId != null)
            .Select(q => new { q.Id, q.TopicId }).ToListAsync(ct);
        var activeIds = bank.Select(q => q.Id).ToHashSet();
        var history = await (from q in db.AttemptQuestions.AsNoTracking()
            join a in db.Attempts.AsNoTracking() on q.AttemptId equals a.Id
            where a.UserId == userId && a.SubmittedAt != null
            orderby a.SubmittedAt descending, a.Id descending
            select new { q.SnapshotJson, q.Answer, a.SubmittedAt }).ToListAsync(ct);
        var latest = history.Select(h => new { Q = JsonSerializer.Deserialize<QuestionSnapshot>(h.SnapshotJson, CentPractice.Json)!, h.Answer, h.SubmittedAt })
            .Where(h => activeIds.Contains(h.Q.Id) && h.Q.TopicId != null).DistinctBy(h => h.Q.Id).ToArray();
        var topicResults = topics.Select(t => {
            var evidence = latest.Where(h => h.Q.TopicId == t.Id).Take(10).ToArray();
            var available = bank.Count(q => q.TopicId == t.Id);
            var correct = evidence.Count(h => h.Answer == h.Q.CorrectAnswer);
            int? accuracy = evidence.Length == 0 ? null : (int)Math.Round(100d * correct / evidence.Length, MidpointRounding.AwayFromZero);
            // Three independent questions are the minimum for a provisional topic estimate.
            var sufficient = evidence.Length >= 3;
            return new TopicMastery(t.Id, t.ChapterId, t.CourseCode, t.Title, available, evidence.Length, correct,
                accuracy, sufficient ? accuracy : null, !sufficient ? evidence.Length == 0 ? "unassessed" : "insufficient-evidence" : accuracy >= 80 ? "strong" : accuracy >= 50 ? "developing" : "needs-study");
        }).ToArray();
        var courses = await db.Courses.AsNoTracking().Where(c => c.Active).OrderBy(c => c.SortOrder).ToListAsync(ct);
        return new {
            method = "latest-distinct-10-v2", minimumEvidence = 3,
            explanation = "Each topic uses the latest submitted practice or assessment answer to up to 10 distinct questions; at least 3 are required. Subject progress is the equal-weight mean across ALL imported topics, with unassessed or insufficient topics contributing zero confirmed progress. It is provisional and covers the supplied curriculum. Reading alone does not count as question evidence.",
            courses = courses.Select(c => {
                var ts = topicResults.Where(t => t.Course == c.Code).ToArray();
                var assessed = ts.Count(t => t.MasteryPercentage != null);
                return new { c.Code, c.Title, c.EnglishTitle, totalTopics = ts.Length, assessedTopics = assessed,
                    coveragePercentage = ts.Length == 0 ? 0 : (int)Math.Round(100d * assessed / ts.Length),
                    confirmedProgressPercentage = ts.Length == 0 ? 0 : (int)Math.Round(ts.Sum(t => t.MasteryPercentage ?? 0) / (double)ts.Length),
                    assessedAccuracyPercentage = assessed == 0 ? (int?)null : (int)Math.Round(ts.Where(t => t.MasteryPercentage != null).Average(t => t.MasteryPercentage!.Value)),
                    topics = ts };
            })
        };
    }
}

public static class CentLearningEndpoints
{
    public static void MapCentLearning(this RouteGroupBuilder group)
    {
        group.MapGet("/curriculum", async (CentDbContext db, CancellationToken ct) => Results.Ok(new {
            courses = await db.Courses.AsNoTracking().Where(c => c.Active).OrderBy(c => c.SortOrder).ToListAsync(ct),
            chapters = await db.Chapters.AsNoTracking().OrderBy(c => c.SortOrder).ToListAsync(ct),
            topics = await db.Topics.AsNoTracking().OrderBy(t => t.SortOrder).Select(t => new { t.Id, t.ChapterId, t.CourseCode, t.Title, t.EnglishTitle,
                questionCount = db.Questions.Count(q => q.Active && q.TopicId == t.Id) }).ToListAsync(ct),
            documents = await db.Documents.AsNoTracking().Where(d => d.Published && d.SourceId != null && db.Courses.Any(c => c.Active && c.TrainingId == d.TrainingId))
                .OrderBy(d => d.SortOrder).Select(d => new { d.Id, d.TrainingId, d.SourceId, d.Title, d.PageCount, d.SortOrder }).ToListAsync(ct)
        })); // Public outlines expose metadata; source text still requires purchased access.
        group.MapGet("/me/mastery", async (ClaimsPrincipal p, CentLearning service, CancellationToken ct) => Results.Ok(await service.Mastery(p.UserId(), ct)))
            .RequireAuthorization("CentUser");
        group.MapPost("/admin/curriculum/import", async (ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var result = await new CentCurriculumImporter(db).Import(ct);
            CentAdminEndpoints.Audit(db, p, "IMPORT_CURRICULUM", Guid.Empty);
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        }).RequireAuthorization("CentAdmin");
        group.MapGet("/documents/{id:guid}/source-sections", async (Guid id, ClaimsPrincipal p, CentDbContext db, CentAccess access, CancellationToken ct) => {
            var doc = await access.RequireDocument(p.UserId(), id, ct);
            CentModule.Check(doc.SourceId != null, "این درس منبع متنی ندارد.", 404);
            return Results.Ok(await db.SourceSections.AsNoTracking().Where(s => s.SourceId == doc.SourceId).OrderBy(s => s.SortOrder)
                .Select(s => new { s.Id, s.TopicId, s.Heading, s.SortOrder, s.Text }).ToListAsync(ct));
        }).RequireAuthorization("CentUser");
    }
}
