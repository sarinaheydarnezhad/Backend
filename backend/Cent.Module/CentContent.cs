using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Cent.Module;

public sealed class CentAccess(CentDbContext db)
{
    public async Task<bool> HasSubscription(Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.Entitlements.AnyAsync(e => e.UserId == userId && e.Kind == "SUBSCRIPTION" &&
            e.RevokedAt == null && e.StartsAt <= now && e.ExpiresAt > now, ct);
    }

    public async Task<bool> CanRead(Guid userId, Guid trainingId, CancellationToken ct)
    {
        var training = await db.Trainings.AsNoTracking().SingleOrDefaultAsync(t => t.Id == trainingId && t.Published, ct);
        if (training is null) return false;
        var now = DateTimeOffset.UtcNow;
        return await db.Entitlements.AnyAsync(e => e.UserId == userId && e.RevokedAt == null && e.StartsAt <= now &&
            (e.ExpiresAt == null || e.ExpiresAt > now) &&
            ((e.Kind == "TRAINING" && e.TrainingId == trainingId) || (training.IncludedInSubscription && e.Kind == "SUBSCRIPTION")), ct);
    }

    public async Task<CentDocument> RequireDocument(Guid userId, Guid documentId, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == documentId && d.Published, ct);
        CentModule.Check(document is not null, "این درس پیدا نشد.", 404);
        CentModule.Check(await CanRead(userId, document!.TrainingId, ct), "برای مطالعه این آموزش، خرید یا اشتراک فعال لازم است.", 403, "access_required");
        return document;
    }
}

public static class CentContentEndpoints
{
    public sealed record NoteInput(Guid DocumentId, int Page, double AnchorY, string Body, long Revision);
    public sealed record ProgressInput(int Page);

    public static void MapCentContent(this RouteGroupBuilder group)
    {
        group.MapGet("/catalog", async (CentDbContext db, CancellationToken ct) => {
            var trainings = await db.Trainings.AsNoTracking().Where(t => t.Published).OrderBy(t => t.Title).ToListAsync(ct);
            var products = await db.Products.AsNoTracking().Where(p => p.Active && (p.Kind == "SUBSCRIPTION" ||
                db.Trainings.Any(t => t.Id == p.TrainingId && t.Published))).OrderBy(p => p.PriceRial).ToListAsync(ct);
            return Results.Ok(new { trainings, products });
        });
        var me = group.MapGroup("/me").RequireAuthorization("CentUser");
        me.MapGet("/library", async (ClaimsPrincipal p, CentDbContext db, CentAccess access, CancellationToken ct) => {
            var userId = p.UserId();
            var grants = await db.Entitlements.AsNoTracking().Where(e => e.UserId == userId).ToListAsync(ct);
            var progress = await db.Progress.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
            var notes = await db.Notes.AsNoTracking().Where(n => n.UserId == userId).GroupBy(n => n.DocumentId)
                .Select(g => new { documentId = g.Key, count = g.Count() }).ToListAsync(ct);
            var hasSubscription = await access.HasSubscription(userId, ct);
            var ownedIds = grants.Where(e => e.Kind == "TRAINING").Select(e => e.TrainingId).ToArray();
            var visitedDocs = progress.Select(x => x.DocumentId).Concat(notes.Select(n => n.documentId)).ToArray();
            var visitedTrainings = db.Documents.Where(d => visitedDocs.Contains(d.Id)).Select(d => d.TrainingId);
            var trainings = await db.Trainings.AsNoTracking().Where(t => ownedIds.Contains(t.Id) || visitedTrainings.Contains(t.Id) ||
                (hasSubscription && t.IncludedInSubscription && t.Published)).OrderBy(t => t.Title).ToListAsync(ct);
            var ids = trainings.Select(t => t.Id).ToArray();
            var documents = await db.Documents.AsNoTracking().Where(d => ids.Contains(d.TrainingId) && d.Published)
                .OrderBy(d => d.SortOrder).Select(d => new { d.Id, d.TrainingId, d.SourceId, d.Title, d.PageCount, d.SortOrder }).ToListAsync(ct);
            var readable = new List<Guid>();
            foreach (var t in trainings) if (await access.CanRead(userId, t.Id, ct)) readable.Add(t.Id);
            return Results.Ok(new { trainings, documents, progress, notes, entitlements = grants, hasSubscription, readableTrainingIds = readable });
        });
        me.MapGet("/notes", async (ClaimsPrincipal p, CentDbContext db, CancellationToken ct) =>
            Results.Ok(await (from n in db.Notes.AsNoTracking() join d in db.Documents on n.DocumentId equals d.Id
                where n.UserId == p.UserId() orderby n.UpdatedAt descending
                select new { n.Id, n.DocumentId, documentTitle = d.Title, n.Page, n.AnchorY, n.Body, n.Revision, n.UpdatedAt }).Take(1000).ToListAsync(ct)));

        var documents = group.MapGroup("/documents").RequireAuthorization("CentUser");
        documents.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal p, CentDbContext db, CentAccess access, CancellationToken ct) => {
            var document = await access.RequireDocument(p.UserId(), id, ct);
            var sections = await db.Sections.AsNoTracking().Where(s => s.DocumentId == id).OrderBy(s => s.StartPage).ToListAsync(ct);
            if (document.SourceId is not null) {
                var sourceSections = await db.SourceSections.AsNoTracking().Where(s => s.SourceId == document.SourceId).OrderBy(s => s.SortOrder).ToListAsync(ct);
                sections = sourceSections.Select((s, i) => new CentSection { Title = s.Heading, StartPage = i + 1 }).ToList();
            }
            var notes = await db.Notes.AsNoTracking().Where(n => n.DocumentId == id && n.UserId == p.UserId()).OrderBy(n => n.Page).ToListAsync(ct);
            var progress = await db.Progress.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == p.UserId() && x.DocumentId == id, ct);
            return Results.Ok(new { document, sections, notes, progress, watermark = $"{p.FindFirstValue(ClaimTypes.Email)} · {p.UserId().ToString()[..8]}" });
        });
        documents.MapGet("/{id:guid}/pages/{page:int}", async (Guid id, int page, ClaimsPrincipal p, CentDbContext db, CentAccess access, HttpContext http, CancellationToken ct) => {
            var doc = await access.RequireDocument(p.UserId(), id, ct);
            CentModule.Check(page >= 1 && page <= doc.PageCount, "صفحه پیدا نشد.", 404);
            var bytes = await db.Pages.Where(x => x.DocumentId == id && x.Number == page).Select(x => x.Image).SingleOrDefaultAsync(ct);
            CentModule.Check(bytes is not null, "صفحه پیدا نشد.", 404);
            http.Response.Headers["Content-Disposition"] = "inline";
            return Results.Bytes(bytes!, "image/webp", enableRangeProcessing: false);
        }).RequireRateLimiting("cent-pages");
        documents.MapPut("/{id:guid}/progress", async (Guid id, ProgressInput input, ClaimsPrincipal p, CentDbContext db, CentAccess access, CancellationToken ct) => {
            var doc = await access.RequireDocument(p.UserId(), id, ct);
            CentModule.Check(input.Page >= 1 && input.Page <= doc.PageCount, "شماره صفحه معتبر نیست.");
            var row = await db.Progress.SingleOrDefaultAsync(x => x.UserId == p.UserId() && x.DocumentId == id, ct);
            if (row is null) { row = new CentProgress { UserId = p.UserId(), DocumentId = id }; db.Progress.Add(row); }
            row.LastPage = input.Page; row.FurthestPage = Math.Max(row.FurthestPage, input.Page); row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct); return Results.Ok(row);
        });
        group.MapPut("/notes/{id:guid}", async (Guid id, NoteInput input, ClaimsPrincipal p, CentDbContext db, CentAccess access, CancellationToken ct) => {
            var doc = await access.RequireDocument(p.UserId(), input.DocumentId, ct);
            CentModule.Check(id != Guid.Empty && input.Page >= 1 && input.Page <= doc.PageCount && double.IsFinite(input.AnchorY) && input.AnchorY >= 0 && input.AnchorY <= 1,
                "محل یادداشت معتبر نیست.");
            CentModule.Check(!string.IsNullOrWhiteSpace(input.Body) && input.Body.Length <= 4000, "یادداشت باید بین ۱ تا ۴۰۰۰ نویسه باشد.");
            var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == id, ct);
            if (note is not null) {
                CentModule.Check(note.UserId == p.UserId(), "یادداشت پیدا نشد.", 404);
                CentModule.Check(note.DocumentId == input.DocumentId && note.Page == input.Page, "محل یادداشت قابل تغییر نیست.");
                if (note.Body == input.Body.Trim() && note.AnchorY == input.AnchorY) return Results.Ok(note);
                CentModule.Check(note.Revision == input.Revision, "یادداشت تغییر کرده؛ دوباره بارگذاری کنید.", 409, "revision_conflict");
                note.Revision++;
            } else {
                CentModule.Check(input.Revision == 0, "این یادداشت حذف شده است.", 409);
                note = new CentNote { Id = id, UserId = p.UserId(), DocumentId = input.DocumentId, Page = input.Page }; db.Notes.Add(note);
            }
            note.Body = input.Body.Trim(); note.AnchorY = input.AnchorY; note.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct); return Results.Ok(note);
        }).RequireAuthorization("CentUser");
        group.MapDelete("/notes/{id:guid}", async (Guid id, long revision, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var deleted = await db.Notes.Where(n => n.Id == id && n.UserId == p.UserId() && n.Revision == revision).ExecuteDeleteAsync(ct);
            return deleted == 1 ? Results.NoContent() : Results.Conflict(new { message = "یادداشت تغییر کرده یا حذف شده است." });
        }).RequireAuthorization("CentUser");
    }
}
