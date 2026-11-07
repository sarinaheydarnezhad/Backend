using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Cent.Module;

public sealed record QuestionInput(string Subject, string Topic, string Difficulty, string Text, string[] Options, int CorrectAnswer, string Explanation, string Kind, bool Active, Guid? TopicId = null);
public sealed record QuestionSnapshot(Guid Id, string Subject, string Topic, string Difficulty, string Text, string[] Options, int CorrectAnswer, string Explanation, Guid? TopicId = null, Guid? SourceSectionId = null, string? FigureSvg = null);
public sealed class CentPractice(CentDbContext db, CentAccess access)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<object> Start(Guid userId, string kind, Guid requestId, CancellationToken ct, string? course = null, Guid? topicId = null)
    {
        CentModule.Check(requestId != Guid.Empty && kind is "diagnostic" or "simulator" or "practice", "نوع آزمون معتبر نیست.");
        CentModule.Check(course is null || Curriculum.CurriculumIdentity.Courses.Any(c => c.Code == course), "درس معتبر نیست.");
        if (topicId is not null) CentModule.Check(await db.Topics.AnyAsync(t => t.Id == topicId && (course == null || t.CourseCode == course), ct), "موضوع معتبر نیست.");
        if (kind == "simulator") CentModule.Check(await access.HasSubscription(userId, ct), "برای شبیه‌ساز اشتراک فعال لازم است.", 403);
        var attempt = await db.Attempts.SingleOrDefaultAsync(a => a.UserId == userId && a.RequestId == requestId, ct);
        if (attempt is not null) CentModule.Check(attempt.Kind == kind && attempt.CourseCodesJson == JsonSerializer.Serialize(course is null ? Array.Empty<string>() : new[] { course }) && attempt.AssessmentMode == (topicId?.ToString() ?? "overview"), "شناسه درخواست تکراری است.", 409);
        if (attempt is null) {
            var day = DateTimeOffset.UtcNow.AddDays(-1);
            CentModule.Check(await db.Attempts.CountAsync(a => a.UserId == userId && a.CreatedAt > day, ct) < 20, "سقف روزانه آزمون‌ها تکمیل شده است.", 429);
            var candidates = await db.Questions.AsNoTracking().Where(q => q.Active && (q.Kind == kind || q.Kind == "both") &&
                (course == null || q.Subject == course) && (topicId == null || q.TopicId == topicId)).ToListAsync(ct);
            // Round-robin across subjects and topics; randomize within each skill and vary difficulty.
            var groups = candidates.GroupBy(q => q.Subject).OrderBy(_ => Random.Shared.Next()).Select(g =>
                new Queue<CentQuestion>(g.GroupBy(q => q.TopicId?.ToString() ?? q.Topic).OrderBy(_ => Random.Shared.Next())
                    .SelectMany(t => t.OrderBy(_ => Random.Shared.Next())
                        .Select((q, i) => (q, i))).OrderBy(x => x.i).Select(x => x.q))).ToArray();
            var questions = new List<CentQuestion>();
            while (questions.Count < 20 && groups.Any(g => g.Count > 0))
                foreach (var g in groups) if (g.Count > 0 && questions.Count < 20) questions.Add(g.Dequeue());
            CentModule.Check(questions.Count > 0, "سؤالات این آزمون هنوز منتشر نشده‌اند.", 409, "exam_not_ready");
            attempt = new CentAttempt { UserId = userId, Kind = kind, RequestId = requestId,
                CourseCodesJson = JsonSerializer.Serialize(course is null ? Array.Empty<string>() : new[] { course }), AssessmentMode = topicId?.ToString() ?? "overview" };
            db.Attempts.Add(attempt);
            for (var i = 0; i < questions.Count; i++) {
                var q = questions[i];
                var snapshot = new QuestionSnapshot(q.Id, q.Subject, q.Topic, q.Difficulty, q.Text,
                    JsonSerializer.Deserialize<string[]>(q.OptionsJson)!, q.CorrectAnswer, q.Explanation, q.TopicId, q.SourceSectionId, q.FigureSvg);
                db.AttemptQuestions.Add(new CentAttemptQuestion { AttemptId = attempt.Id, Position = i, QuestionId = q.Id,
                    SnapshotJson = JsonSerializer.Serialize(snapshot, Json) });
            }
            await db.SaveChangesAsync(ct);
        }
        var rows = await db.AttemptQuestions.AsNoTracking().Where(q => q.AttemptId == attempt.Id).OrderBy(q => q.Position).ToListAsync(ct);
        return new { attempt.Id, attempt.Kind, attempt.CreatedAt, expiresAt = attempt.CreatedAt.AddMinutes(60),
            questions = rows.Select(r => { var q = JsonSerializer.Deserialize<QuestionSnapshot>(r.SnapshotJson, Json)!;
                return new { q.Id, q.Subject, q.Topic, q.Difficulty, q.Text, q.Options, q.TopicId, q.SourceSectionId, q.FigureSvg }; }) };
    }

    public async Task<object> Submit(Guid userId, Guid id, Dictionary<Guid, int?> answers, CancellationToken ct)
    {
        var attempt = await db.Attempts.SingleOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        CentModule.Check(attempt is not null, "آزمون پیدا نشد.", 404);
        if (attempt!.SubmittedAt is not null) return await Result(userId, id, ct);
        CentModule.Check(DateTimeOffset.UtcNow <= attempt.CreatedAt.AddMinutes(60), "زمان این تلاش تمام شده؛ آزمون تازه‌ای شروع کنید.", 409);
        if (attempt.Kind == "simulator") CentModule.Check(await access.HasSubscription(userId, ct), "اشتراک فعال لازم است.", 403);
        var rows = await db.AttemptQuestions.Where(q => q.AttemptId == id).OrderBy(q => q.Position).ToListAsync(ct);
        var questions = rows.Select(r => JsonSerializer.Deserialize<QuestionSnapshot>(r.SnapshotJson, Json)!).ToArray();
        CentModule.Check(answers.Count <= questions.Length && answers.Keys.All(key => questions.Any(q => q.Id == key)) &&
            questions.All(q => !answers.TryGetValue(q.Id, out var a) || a is null || (a >= 0 && a < q.Options.Length)), "پاسخ‌ها معتبر نیستند.");
        var result = Score(id, attempt.CreatedAt, questions, answers);
        var analysis = Analyze(result);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Attempts.Where(a => a.Id == id && a.SubmittedAt == null).ExecuteUpdateAsync(u =>
            u.SetProperty(a => a.SubmittedAt, DateTimeOffset.UtcNow).SetProperty(a => a.ResultJson, JsonSerializer.Serialize(result, Json)), ct);
        if (claimed == 0) { await transaction.RollbackAsync(ct); db.ChangeTracker.Clear(); return await Result(userId, id, ct); }
        foreach (var row in rows) row.Answer = answers.GetValueOrDefault(row.QuestionId);
        db.Analyses.Add(new CentAiAnalysis { AttemptId = id, Provider = "rules", PayloadJson = JsonSerializer.Serialize(analysis, Json) });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new { result, analysis, review = questions.Select(q => new { q.Id, q.CorrectAnswer, q.Explanation, q.Topic }) };
    }

    public async Task<object> Result(Guid userId, Guid id, CancellationToken ct)
    {
        var attempt = await db.Attempts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.UserId == userId && a.SubmittedAt != null, ct);
        CentModule.Check(attempt is not null, "نتیجه پیدا نشد.", 404);
        var analysis = await db.Analyses.AsNoTracking().SingleAsync(a => a.AttemptId == id, ct);
        var rows = await db.AttemptQuestions.AsNoTracking().Where(q => q.AttemptId == id).OrderBy(q => q.Position).ToListAsync(ct);
        return new { result = JsonSerializer.Deserialize<JsonElement>(attempt!.ResultJson!), analysis = JsonSerializer.Deserialize<JsonElement>(analysis.PayloadJson),
            review = rows.Select(r => { var q = JsonSerializer.Deserialize<QuestionSnapshot>(r.SnapshotJson, Json)!; return new { q.Id, q.CorrectAnswer, q.Explanation, q.Topic }; }) };
    }

    public sealed record Breakdown(int Total, int Correct, int Incorrect, int Unanswered, int Percentage);
    public sealed record TopicBreakdown(int Total, int Correct, int Incorrect, int Unanswered, int Percentage, string Subject);
    public sealed record ScoredResult(Guid AttemptId, DateTimeOffset CreatedAt, int Total, int Correct, int Incorrect, int Unanswered, int Percentage,
        Dictionary<string, Breakdown> BySubject, Dictionary<string, TopicBreakdown> ByTopic, Dictionary<string, Breakdown> ByDifficulty, Dictionary<Guid, int?> Answers);
    internal static ScoredResult Score(Guid id, DateTimeOffset date, QuestionSnapshot[] questions, Dictionary<Guid, int?> answers)
    {
        Breakdown Count(IEnumerable<QuestionSnapshot> input) {
            var qs = input.ToArray(); var correct = qs.Count(q => answers.GetValueOrDefault(q.Id) == q.CorrectAnswer);
            var blank = qs.Count(q => answers.GetValueOrDefault(q.Id) == null);
            return new(qs.Length, correct, qs.Length - correct - blank, blank, qs.Length == 0 ? 0 : (int)Math.Round(100d * correct / qs.Length, MidpointRounding.AwayFromZero));
        }
        var total = Count(questions);
        return new(id, date, total.Total, total.Correct, total.Incorrect, total.Unanswered, total.Percentage,
            Curriculum.CurriculumIdentity.Courses.Select(c => c.Code).ToDictionary(s => s, s => Count(questions.Where(q => q.Subject == s))),
            questions.GroupBy(q => q.Subject + ": " + q.Topic).ToDictionary(g => g.Key, g => { var n = Count(g); return new TopicBreakdown(n.Total, n.Correct, n.Incorrect, n.Unanswered, n.Percentage, g.First().Subject); }),
            new[] { "easy", "medium", "hard" }.ToDictionary(s => s, s => Count(questions.Where(q => q.Difficulty == s))), answers);
    }
    internal static object Analyze(ScoredResult r)
    {
        var weak = r.ByTopic.OrderBy(x => x.Value.Percentage).Where(x => x.Value.Percentage < 60).Select(x => x.Key).Take(4).ToArray();
        var strong = r.ByTopic.OrderByDescending(x => x.Value.Percentage).Where(x => x.Value.Percentage >= 70).Select(x => x.Key).Take(3).ToArray();
        return new { overallAssessment = "نتیجه از پاسخ‌های ثبت‌شده و کلید پاسخ سرور محاسبه شده است.", strengths = strong,
            weaknesses = weak, priorityTopics = weak.Take(3), recommendedStudyAreas = weak.Select(t => "مرور و تمرین «" + t + "»"),
            suggestedNextStep = weak.Length > 0 ? "مطالعه را از «" + weak[0] + "» شروع کنید." : "تمرین ترکیبی را ادامه دهید.",
            personalizedExplanation = "این پیشنهاد با قواعد آموزشی ثابت و بدون ارسال اطلاعات شما به سرویس خارجی تهیه شده است.", provider = "rules", status = "COMPLETED" };
    }
}

public static class CentPracticeEndpoints
{
    public sealed record StartInput(string Kind, Guid RequestId, string? Course = null, Guid? TopicId = null);
    public sealed record SubmitInput(Dictionary<Guid, int?> Answers);
    public static void MapCentPractice(this RouteGroupBuilder group)
    {
        var attempts = group.MapGroup("/attempts").RequireAuthorization("CentUser");
        attempts.MapPost("/", async (StartInput input, ClaimsPrincipal p, CentPractice service, CancellationToken ct) => Results.Ok(await service.Start(p.UserId(), input.Kind, input.RequestId, ct, input.Course, input.TopicId)));
        attempts.MapPost("/{id:guid}/submit", async (Guid id, SubmitInput input, ClaimsPrincipal p, CentPractice service, CancellationToken ct) => {
            CentModule.Check(input.Answers is not null, "پاسخ‌ها لازم است."); return Results.Ok(await service.Submit(p.UserId(), id, input.Answers!, ct));
        });
        attempts.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal p, CentPractice service, CancellationToken ct) => Results.Ok(await service.Result(p.UserId(), id, ct)));
        attempts.MapGet("/", async (ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => Results.Ok(await db.Attempts.AsNoTracking()
            .Where(a => a.UserId == p.UserId() && a.SubmittedAt != null).OrderByDescending(a => a.CreatedAt)
            .Select(a => new { a.Id, a.Kind, a.CreatedAt, a.ResultJson }).Take(100).ToListAsync(ct)));
        var admin = group.MapGroup("/admin/questions").RequireAuthorization("CentAdmin");
        admin.MapGet("/", async (CentDbContext db, CancellationToken ct) => Results.Ok((await db.Questions.AsNoTracking().OrderBy(q => q.Topic).ToListAsync(ct))
            .Select(q => new { q.Id, q.Subject, q.Topic, q.TopicId, q.Difficulty, q.Text, options = JsonSerializer.Deserialize<string[]>(q.OptionsJson), q.CorrectAnswer, q.Explanation, q.Kind, q.Active })));
        admin.MapPut("/{id:guid}", async (Guid id, QuestionInput input, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            CentModule.Check(id != Guid.Empty && Curriculum.CurriculumIdentity.Courses.Any(c => c.Code == input.Subject) && input.Difficulty is "easy" or "medium" or "hard" && input.Kind is "diagnostic" or "simulator" or "both", "نوع سؤال معتبر نیست.");
            CentModule.Check(input.Text is { Length: > 0 and <= 8000 } && input.Topic is { Length: > 0 and <= 500 } && input.Explanation is { Length: <= 8000 } &&
                input.Options is { Length: >= 2 and <= 6 } && input.Options.All(s => s is { Length: > 0 and <= 1000 }) && input.CorrectAnswer >= 0 && input.CorrectAnswer < input.Options.Length, "متن و گزینه‌ها معتبر نیستند.");
            var row = await db.Questions.SingleOrDefaultAsync(q => q.Id == id, ct);
            if (row is null) { row = new CentQuestion { Id = id }; db.Questions.Add(row); }
            if (input.TopicId is not null) CentModule.Check(await db.Topics.AnyAsync(t => t.Id == input.TopicId && t.CourseCode == input.Subject, ct), "موضوع با درس انتخاب‌شده سازگار نیست.");
            row.TopicId = input.TopicId;
            row.Subject = input.Subject; row.Topic = input.Topic.Trim(); row.Difficulty = input.Difficulty; row.Kind = input.Kind; row.Text = input.Text;
            row.OptionsJson = JsonSerializer.Serialize(input.Options); row.CorrectAnswer = input.CorrectAnswer; row.Explanation = input.Explanation; row.Active = input.Active;
            CentAdminEndpoints.Audit(db, p, "SAVE_QUESTION", id); await db.SaveChangesAsync(ct); return Results.Ok(new { row.Id });
        });
    }
}
