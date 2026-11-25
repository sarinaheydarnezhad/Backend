using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Cent.Module;

// Review never changes publication, entitlements, notes or student progress.
public static class CentContentPreview
{
    public static void MapContentPreview(this RouteGroupBuilder group)
    {
        var routes = group.MapGroup("/admin/documents/{id:guid}/preview").RequireAuthorization("CentAdmin:content");
        routes.MapGet("/", async (Guid id, CentDbContext db, CancellationToken ct) => {
            var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            CentModule.Check(document?.SourceId != null, "HTML document not found.", 404);
            return Results.Ok(new { document, sections = await db.SourceSections.AsNoTracking()
                .Where(s => s.SourceId == document!.SourceId).OrderBy(s => s.SortOrder)
                .Select(s => new { s.Id, s.Heading, s.SortOrder }).ToArrayAsync(ct) });
        });
        routes.MapGet("/{sectionId:guid}", async (Guid id, Guid sectionId, ClaimsPrincipal p, CentDbContext db,
            CentReaderProtection protection, HttpContext http, CancellationToken ct) => {
            await using var lease = await protection.Enter(p.UserId(), http.Connection.RemoteIpAddress?.ToString() ?? "unknown", sectionId, false, ct);
            var fragment = await Fragment(db, id, sectionId, ct);
            var trace = new CentAuditEvent { ActorId = p.UserId(), EntityId = sectionId, Action = "ADMIN_CONTENT_PREVIEW" };
            db.Audit.Add(trace); await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = sectionId, pilotHtml = fragment.Html, contentRevision = fragment.Revision,
                assets = fragment.Assets.Select(a => new { a.Id, a.Mime, a.Alt }), watermark = "IT-" + trace.Id.ToString("N") });
        });
        routes.MapGet("/{sectionId:guid}/assets/{assetId}", async (Guid id, Guid sectionId, string assetId, ClaimsPrincipal p,
            CentDbContext db, CentReaderProtection protection, HttpContext http, CancellationToken ct) => {
            await using var lease = await protection.Enter(p.UserId(), http.Connection.RemoteIpAddress?.ToString() ?? "unknown", sectionId, true, ct);
            var fragment = await Fragment(db, id, sectionId, ct);
            var asset = fragment.Assets.SingleOrDefault(a => a.Id == assetId);
            CentModule.Check(asset != null, "Asset not found.", 404);
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            return Results.Bytes(asset!.Bytes, asset.Mime, enableRangeProcessing: false);
        });
    }

    private static async Task<ReaderFragment> Fragment(CentDbContext db, Guid id, Guid sectionId, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        CentModule.Check(document?.SourceId != null, "HTML document not found.", 404);
        var section = await db.SourceSections.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sectionId && s.SourceId == document!.SourceId, ct);
        CentModule.Check(section != null, "Section not found.", 404);
        var source = await db.Sources.AsNoTracking().SingleAsync(s => s.Id == document!.SourceId, ct);
        return await CentReviewedContent.Imported(db, ct) ? CentEducationalContent.Fragment(section!.Html, source.ImportKey)
            : CentEducationalContent.Section(section!.ImportKey, source.OriginalHtml);
    }
}
