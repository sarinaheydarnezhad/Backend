using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Cent.Module.Curriculum;

namespace Cent.Module;

public sealed record TopicMastery(Guid Id, Guid ChapterId, string Course, string Title, int AvailableQuestions,
    int EvidenceQuestions, int Correct, int? AccuracyPercentage, int? MasteryPercentage, string Status);

public sealed class CentLearning(CentDbContext db)
{
    // The latest answer to each distinct question counts once. Repetition cannot increase evidence.
    // Submitted practice and assessment answers both contribute to provisional progress.
    public async Task<object> Mastery(Guid userId, CancellationToken ct, string? syllabusRevisionId = null)
    {
        var topics = await db.Topics.AsNoTracking().OrderBy(t => t.SortOrder).ToListAsync(ct);
        var release = CentCurriculumImporter.Bundled().Release;
        var newTopicIds = release?.Resources.Where(r => r.Kind == "lesson").Select(r => CurriculumIdentity.Id("topic:" + r.TopicKey)).ToHashSet() ?? [];
        var newReviewedTopics = CentReviewedContent.Content.Changes.Where(c => c.Entity == "topics" && c.Before is null).Select(c => c.Id).ToHashSet();
        if (syllabusRevisionId is null) topics = topics.Where(t => !newTopicIds.Contains(t.Id) && !newReviewedTopics.Contains(t.Id)).ToList();
        else {
            CentModule.Check(release is not null && release.SyllabusRevisionId == syllabusRevisionId, "نسخهٔ سرفصل پیدا نشد.", 404);
            topics = topics.Where(t => newTopicIds.Contains(t.Id)).ToList();
        }
        var bank = await db.Questions.AsNoTracking().Where(q => q.Active && q.TopicId != null)
            .Select(q => new { q.Id, q.TopicId }).ToListAsync(ct);
        var activeIds = bank.Select(q => q.Id).ToHashSet();
        var history = await (from q in db.AttemptQuestions.AsNoTracking()
            join a in db.Attempts.AsNoTracking() on q.AttemptId equals a.Id
            where a.UserId == userId && a.SubmittedAt != null && a.Kind != "mock"
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
            method = "latest-distinct-10-v2", syllabusRevisionId, minimumEvidence = 3,
            explanation = "Each topic uses the latest submitted practice or assessment answer to up to 10 distinct questions; at least 3 are required. Subject progress is the equal-weight mean across topics in the selected curriculum scope; without a syllabus revision the original topic scope is retained. Unassessed topics contribute zero. Reading material alone does not count as question evidence. New Mock Exams do not contribute.",
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
            syllabusRevisionId = await db.CurriculumRevisions.AsNoTracking()
                .Where(r => r.Id == CurriculumReleases.BundledReleaseId).Select(r => r.SyllabusRevisionId).SingleOrDefaultAsync(ct),
            courses = await db.Courses.AsNoTracking().Where(c => c.Active).OrderBy(c => c.SortOrder).Select(c => new {
                c.Code, c.Title, c.EnglishTitle, c.SortOrder, c.TrainingId, c.Active,
                supportsLegacyAssessment = db.Questions.Any(q => q.Active && q.Subject == c.Code && (q.Kind == "both" || q.Kind == "diagnostic" || q.Kind == "simulator"))
            }).ToListAsync(ct),
            chapters = await db.Chapters.AsNoTracking().Where(c => !db.CurriculumImports.Any(i => i.PackageHash == CentReviewedContent.Hash) || !CentReviewedContent.Content.ArchivedChapterIds.Contains(c.Id)).OrderBy(c => c.SortOrder).ToListAsync(ct),
            topics = await db.Topics.AsNoTracking().Where(t => !db.CurriculumImports.Any(i => i.PackageHash == CentReviewedContent.Hash) || !CentReviewedContent.Content.ArchivedTopicIds.Contains(t.Id)).OrderBy(t => t.SortOrder).Select(t => new { t.Id, t.ChapterId, t.CourseCode, t.Title, t.EnglishTitle,
                contentContract = db.Questions.Any(q => q.Active && q.TopicId == t.Id && q.Kind == "practice") ? "passage-v1" : null,
                questionCount = db.Questions.Count(q => q.Active && q.TopicId == t.Id) }).ToListAsync(ct),
            documents = await db.Documents.AsNoTracking().Where(d => d.Published && d.SourceId != null && (!db.CurriculumImports.Any(i => i.PackageHash == CentReviewedContent.Hash) || !CentReviewedContent.Content.ArchivedSourceIds.Contains(d.SourceId.Value)) && db.Courses.Any(c => c.Active && c.TrainingId == d.TrainingId))
                .OrderBy(d => d.SortOrder).Select(d => new { d.Id, d.TrainingId, d.SourceId, d.Title, d.PageCount, d.SortOrder }).ToListAsync(ct)
        })); // Public outlines expose metadata; source text still requires purchased access.
        group.MapGet("/me/mastery", async (ClaimsPrincipal p, CentLearning service, CancellationToken ct, string? syllabusRevisionId) => Results.Ok(await service.Mastery(p.UserId(), ct, syllabusRevisionId)))
            .RequireAuthorization("CentUser");
        group.MapPost("/admin/curriculum/import", async (ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var result = await new CentCurriculumImporter(db).Import(ct);
            CentAdminEndpoints.Audit(db, p, "IMPORT_CURRICULUM", Guid.Empty);
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        }).RequireAuthorization("CentAdmin:content");
        group.MapGet("/documents/{id:guid}/source-sections", async (Guid id, ClaimsPrincipal p, CentDbContext db, CentAccess access, Microsoft.Extensions.Configuration.IConfiguration config, CancellationToken ct) => {
            var doc = await access.RequireDocument(p.UserId(), id, ct);
            CentModule.Check(doc.SourceId != null, "این درس منبع متنی ندارد.", 404);
            var general = await CentReaderRollout.General(db, config, doc, ct);
            var granular = general || await CentReaderRollout.Granular(db, config, doc, ct);
            var rows = await db.SourceSections.AsNoTracking().Where(s => s.SourceId == doc.SourceId).OrderBy(s => s.SortOrder)
                .Select(s => new { s.Id, s.TopicId, s.Heading, s.SortOrder, text = granular ? "" : s.Text, delivery = granular ? "section-v1" : null,
                    pilot = general || granular && s.ImportKey == CentReaderPilot.SectionKey }).ToListAsync(ct);
            return Results.Ok(rows);
        }).RequireAuthorization("CentUser");
        group.MapGet("/documents/{id:guid}/source-sections/{sectionId:guid}", async (Guid id, Guid sectionId, ClaimsPrincipal p, CentDbContext db, CentAccess access,
            Microsoft.Extensions.Configuration.IConfiguration config, CentPilotProtection protection, CentReaderProtection readerProtection, HttpContext http, CancellationToken ct) => {
            var doc = await access.RequireDocument(p.UserId(), id, ct);
            CentModule.Check(doc.SourceId != null && await CentReaderRollout.Granular(db, config, doc, ct), "Section delivery unavailable.", 404);
            var general = await CentReaderRollout.General(db, config, doc, ct);
            using var permit = general ? null : protection.Enter(p.UserId(), http.Connection.RemoteIpAddress?.ToString() ?? "unknown", sectionId);
            await using var readerPermit = general ? await readerProtection.Enter(p.UserId(), http.Connection.RemoteIpAddress?.ToString() ?? "unknown", sectionId, false, ct) : null;
            var section = await db.SourceSections.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sectionId && s.SourceId == doc.SourceId, ct);
            CentModule.Check(section is not null, "Section not found.", 404);
            var pilot = general || section!.ImportKey == CentReaderPilot.SectionKey;
            var source = general ? await db.Sources.AsNoTracking().SingleAsync(s => s.Id == doc.SourceId, ct) : null;
            var fragment = general ? await CentReviewedContent.Imported(db, ct)
                ? CentEducationalContent.Fragment(section!.Html, source!.ImportKey)
                : CentEducationalContent.Section(section!.ImportKey, source!.OriginalHtml) : null;
            // Existing private audit storage provides durable account-to-trace association, without PII in the reader.
            var trace = new CentAuditEvent { ActorId = p.UserId(), EntityId = sectionId, Action = general ? "READER_SECTION_DELIVERY" : "PILOT_SECTION_DELIVERY" };
            db.Audit.Add(trace); await db.SaveChangesAsync(ct);
            await Results.Ok(new { section!.Id, section.TopicId, section.Heading, section.SortOrder,
                text = pilot ? "" : section.Text, pilotHtml = fragment?.Html ?? (pilot ? CentReaderPilot.Html() : null),
                contentRevision = fragment?.Revision ?? (pilot ? CentReaderPilot.Revision : CentModule.Hash(section.Text)[..24]),
                assets = fragment?.Assets.Select(a => new { a.Id, a.Mime, a.Alt }), findings = fragment?.Findings, subject = source?.CourseCode,
                watermark = "IT-" + trace.Id.ToString("N") }).ExecuteAsync(http);
            return Results.Empty;
        }).RequireAuthorization("CentUser");

        group.MapGet("/documents/{id:guid}/source-sections/{sectionId:guid}/assets/{assetId}", async (Guid id, Guid sectionId, string assetId,
            ClaimsPrincipal p, CentDbContext db, CentAccess access, Microsoft.Extensions.Configuration.IConfiguration config,
            CentReaderProtection protection, HttpContext http, CancellationToken ct) => {
            var doc = await access.RequireDocument(p.UserId(), id, ct);
            CentModule.Check(await CentReaderRollout.General(db, config, doc, ct) && doc.SourceId != null, "Asset unavailable.", 404);
            await using var permit = await protection.Enter(p.UserId(), http.Connection.RemoteIpAddress?.ToString() ?? "unknown", sectionId, true, ct);
            var section = await db.SourceSections.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sectionId && s.SourceId == doc.SourceId, ct);
            CentModule.Check(section is not null, "Section not found.", 404);
            var source = await db.Sources.AsNoTracking().SingleAsync(s => s.Id == doc.SourceId, ct);
            var fragment = await CentReviewedContent.Imported(db, ct) ? CentEducationalContent.Fragment(section!.Html, source.ImportKey)
                : CentEducationalContent.Section(section!.ImportKey, source.OriginalHtml);
            var asset = fragment.Assets.SingleOrDefault(a => a.Id == assetId);
            CentModule.Check(asset is not null, "Asset not found.", 404);
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers.ContentDisposition = "inline";
            await Results.Bytes(asset!.Bytes, asset.Mime, enableRangeProcessing: false).ExecuteAsync(http);
            return Results.Empty;
        }).RequireAuthorization("CentUser");

    }
}
