using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cent.Module;

public static class CentReviewedAssessments
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Mode(string id) => "v4:" + CentModule.Hash(id)[..32];
    public static object Payload(CentAttempt attempt, QuestionSnapshot[] questions) => new {
        attempt.Id, attempt.Kind, attempt.CreatedAt, expiresAt = attempt.CreatedAt.AddMinutes(attempt.DurationMinutes), contentContract = "passage-v1",
        passages = questions.Where(q => q.PassageId != null).DistinctBy(q => q.PassageId).Select(q => new { id = q.PassageId, text = q.PassageText }),
        questions = questions.Select(q => new { q.Id, q.Subject, q.Topic, q.Difficulty, q.Text, q.Options, q.TopicId, q.SourceSectionId, q.FigureSvg, q.PassageId })
    };
    public static async Task<object> Start(CentDbContext db, CentAccess access, IConfiguration config, Guid user, string id, Guid request, CancellationToken ct)
    {
        var definition = CentReviewedContent.Content.Assessments.SingleOrDefault(a => a.Id == id);
        CentModule.Check(definition != null && CentReaderRollout.Subject(config, definition.Subject) && await CentReviewedContent.Imported(db, ct), "Assessment unavailable.", 404);
        var training = await db.Courses.Where(c => c.Code == definition!.Subject).Select(c => c.TrainingId).SingleAsync(ct);
        CentModule.Check(training != null && await access.CanRead(user, training.Value, ct), "Active subject access required.", 403, "access_required");
        CentModule.Check(request != Guid.Empty, "Invalid request identifier.");
        var mode = Mode(id);
        var attempt = await db.Attempts.SingleOrDefaultAsync(a => a.UserId == user && a.RequestId == request, ct);
        if (attempt != null) CentModule.Check(attempt.AssessmentMode == mode && attempt.Kind == "practice", "Request identifier conflict.", 409);
        else {
            var since = DateTimeOffset.UtcNow.AddDays(-1);
            CentModule.Check(await db.Attempts.CountAsync(a => a.UserId == user && a.CreatedAt > since, ct) < 20, "Daily attempt limit reached.", 429);
            var bank = await db.Questions.AsNoTracking().Where(q => definition!.QuestionIds.Contains(q.Id) && q.Active && q.Subject == definition.Subject).ToDictionaryAsync(q => q.Id, ct);
            CentModule.Check(bank.Count == definition!.QuestionIds.Length, "Assessment content incomplete.", 409);
            attempt = new CentAttempt { UserId = user, RequestId = request, Kind = "practice", AssessmentMode = mode,
                DurationMinutes = definition.DurationMinutes, CourseCodesJson = JsonSerializer.Serialize(new[] { definition.Subject }) };
            db.Add(attempt);
            for (var i = 0; i < definition.QuestionIds.Length; i++) {
                var q = bank[definition.QuestionIds[i]];
                var passage = CentReviewedContent.Content.Passages.SingleOrDefault(p => p.QuestionIds.Contains(q.Id) || p.QuestionKeys.Contains(q.ImportKey));
                if (definition.PassageIds.Length > 0) CentModule.Check(passage != null && definition.PassageIds.Contains(passage.Id), "Missing assessment passage.", 409);
                var figure = q.FigureSvg is null ? null : CentEducationalContent.SafeSvg(CentContentMarkup.Parse(q.FigureSvg).Descendants().First(n => n.Name == "svg"));
                var snapshot = new QuestionSnapshot(q.Id, q.Subject, q.Topic, q.Difficulty, q.Text, JsonSerializer.Deserialize<string[]>(q.OptionsJson)!, q.CorrectAnswer,
                    q.Explanation, q.TopicId, q.SourceSectionId, figure, passage?.Id, null, passage?.Text);
                db.AttemptQuestions.Add(new CentAttemptQuestion { AttemptId = attempt.Id, Position = i, QuestionId = q.Id, SnapshotJson = JsonSerializer.Serialize(snapshot, Json) });
            }
            await db.SaveChangesAsync(ct);
        }
        var rows = await db.AttemptQuestions.AsNoTracking().Where(q => q.AttemptId == attempt.Id).OrderBy(q => q.Position).Select(q => q.SnapshotJson).ToArrayAsync(ct);
        return Payload(attempt, rows.Select(s => JsonSerializer.Deserialize<QuestionSnapshot>(s, Json)!).ToArray());
    }
    public sealed record StartInput(Guid RequestId);
    public static void MapReviewedAssessments(this RouteGroupBuilder group)
    {
        var routes = group.MapGroup("/reviewed-assessments").RequireAuthorization("CentUser");
        routes.MapGet("/", async (ClaimsPrincipal p, CentDbContext db, CentAccess access, IConfiguration config, CancellationToken ct) => {
            var allowed = new HashSet<string>();
            if (await CentReviewedContent.Imported(db, ct)) foreach (var course in await db.Courses.ToArrayAsync(ct))
                if (course.TrainingId is {} training && CentReaderRollout.Subject(config, course.Code) && await access.CanRead(p.UserId(), training, ct)) allowed.Add(course.Code);
            return Results.Ok(CentReviewedContent.Content.Assessments.Where(a => allowed.Contains(a.Subject)).Select(a => new { a.Id, a.Subject, a.Title, a.DurationMinutes, questionCount = a.QuestionIds.Length }));
        });
        routes.MapPost("/{id}/start", async (string id, StartInput input, ClaimsPrincipal p, CentDbContext db, CentAccess access, IConfiguration config, CancellationToken ct) =>
            Results.Ok(await Start(db, access, config, p.UserId(), id, input.RequestId, ct)));
    }
}
