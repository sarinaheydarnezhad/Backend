using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cent.Module;

public static class CentAdminEndpoints
{
    public sealed record TrainingInput(string Title, string Description, bool IncludedInSubscription, bool Published);
    public sealed record ProductInput(string Title, string Kind, Guid? TrainingId, long PriceRial, int? DurationDays, bool Active);
    public sealed record DocumentInput(Guid TrainingId, string Title, int PageCount, int SortOrder);
    public sealed record SectionInput(string Title, int StartPage);
    public sealed record PublishInput(SectionInput[] Sections);

    public static void MapCentAdmin(this RouteGroupBuilder group)
    {
        var admin = group.MapGroup("/admin").RequireAuthorization("CentAdmin");
        admin.MapGet("/overview", async (CentDbContext db, CancellationToken ct) => Results.Ok(new {
            users = await db.Users.CountAsync(ct), trainings = await db.Trainings.CountAsync(ct),
            paidOrders = await db.Orders.CountAsync(o => o.Status == "PAID", ct),
            incomeRial = await db.Orders.Where(o => o.Status == "PAID" && o.Provider == "ZARINPAL").SumAsync(o => (long?)o.AmountRial, ct) ?? 0,
            storageBytes = await db.Pages.SumAsync(p => (long?)p.Image.Length, ct) ?? 0
        }));
        admin.MapGet("/trainings", async (CentDbContext db, CancellationToken ct) => Results.Ok(new {
            trainings = await db.Trainings.AsNoTracking().OrderBy(t => t.Title).ToListAsync(ct),
            documents = await db.Documents.AsNoTracking().OrderBy(d => d.SortOrder).ToListAsync(ct),
            products = await db.Products.AsNoTracking().ToListAsync(ct)
        }));
        admin.MapPut("/trainings/{id:guid}", async (Guid id, TrainingInput input, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            CentModule.Check(id != Guid.Empty && !string.IsNullOrWhiteSpace(input.Title) && input.Title.Length <= 200 && input.Description is { Length: <= 8000 }, "عنوان یا توضیح آموزش معتبر نیست.");
            var row = await db.Trainings.SingleOrDefaultAsync(t => t.Id == id, ct);
            if (row is null) { row = new CentTraining { Id = id }; db.Trainings.Add(row); }
            row.Title = input.Title.Trim(); row.Description = input.Description.Trim(); row.IncludedInSubscription = input.IncludedInSubscription; row.Published = input.Published;
            Audit(db, p, "SAVE_TRAINING", id); await db.SaveChangesAsync(ct); return Results.Ok(row);
        });
        admin.MapPut("/products/{id:guid}", async (Guid id, ProductInput input, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            CentModule.Check(id != Guid.Empty && !string.IsNullOrWhiteSpace(input.Title) && input.Title.Length <= 200 && input.PriceRial is > 0 and <= 1_000_000_000_000,
                "عنوان و قیمت معتبر (به ریال) وارد کنید.");
            CentModule.Check((input.Kind == "TRAINING" && input.TrainingId is not null && input.DurationDays is null) ||
                (input.Kind == "SUBSCRIPTION" && input.TrainingId is null && input.DurationDays is >= 1 and <= 366), "نوع دسترسی معتبر نیست.");
            if (input.TrainingId is not null) CentModule.Check(await db.Trainings.AnyAsync(t => t.Id == input.TrainingId, ct), "آموزش پیدا نشد.", 404);
            var row = await db.Products.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) { row = new CentProduct { Id = id }; db.Products.Add(row); }
            CentModule.Check(!await db.Orders.AnyAsync(o => o.ProductId == id, ct) || (row.Kind == input.Kind && row.TrainingId == input.TrainingId),
                "نوع محصول فروخته‌شده را عوض نکنید؛ محصول جدید بسازید.", 409);
            row.Title = input.Title.Trim(); row.Kind = input.Kind; row.TrainingId = input.TrainingId;
            row.PriceRial = input.PriceRial; row.DurationDays = input.DurationDays; row.Active = input.Active;
            Audit(db, p, "SAVE_PRODUCT", id); await db.SaveChangesAsync(ct); return Results.Ok(row);
        });
        admin.MapPost("/documents", async (DocumentInput input, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            CentModule.Check(input.Title is { Length: > 0 and <= 200 } && input.PageCount is >= 1 and <= 200 && input.SortOrder is >= 0 and <= 10000,
                "عنوان، تعداد صفحات (حداکثر ۲۰۰) و ترتیب درس معتبر نیست.");
            CentModule.Check(await db.Trainings.AnyAsync(t => t.Id == input.TrainingId, ct), "آموزش پیدا نشد.", 404);
            var document = new CentDocument { TrainingId = input.TrainingId, Title = input.Title.Trim(), PageCount = input.PageCount, SortOrder = input.SortOrder };
            db.Documents.Add(document); Audit(db, p, "CREATE_DOCUMENT", document.Id); await db.SaveChangesAsync(ct);
            return Results.Ok(document);
        });
        admin.MapGet("/documents/{id:guid}/upload-state", async (Guid id, CentDbContext db, CancellationToken ct) => {
            var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            CentModule.Check(document is not null, "پیش‌نویس پیدا نشد.", 404);
            return Results.Ok(new { document!.Published, uploadedPages = await db.Pages.Where(p => p.DocumentId == id).Select(p => p.Number).ToListAsync(ct) });
        });
        admin.MapPut("/documents/{id:guid}/pages/{page:int}", async (Guid id, int page, int width, int height, HttpContext http, CentDbContext db, IConfiguration config, CancellationToken ct) => {
            var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
            CentModule.Check(document is not null && !document.Published, "فقط صفحات درس پیش‌نویس قابل بارگذاری هستند.", 409);
            CentModule.Check(page >= 1 && page <= document!.PageCount && width is >= 100 and <= 2400 && height is >= 100 and <= 6000, "ابعاد صفحه معتبر نیست.");
            const int maxBytes = 1_500_000;
            CentModule.Check(http.Request.ContentType == "image/webp" && http.Request.ContentLength is > 0 and <= maxBytes, "هر صفحه باید تصویر WebP با حجم حداکثر ۱٫۵ مگابایت باشد.", 413);
            using var stream = new MemoryStream();
            var buffer = new byte[16384]; int count;
            while ((count = await http.Request.Body.ReadAsync(buffer, ct)) > 0) {
                CentModule.Check(stream.Length + count <= maxBytes, "حجم صفحه بیش از حد مجاز است.", 413);
                stream.Write(buffer, 0, count);
            }
            var bytes = stream.ToArray();
            CentModule.Check(bytes.Length >= 20 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP", "تصویر WebP معتبر نیست.");
            var row = await db.Pages.SingleOrDefaultAsync(x => x.DocumentId == id && x.Number == page, ct);
            var existingBytes = row?.Image.LongLength ?? 0;
            var documentBytes = await db.Pages.Where(x => x.DocumentId == id).SumAsync(x => (long?)x.Image.Length, ct) ?? 0;
            CentModule.Check(documentBytes - existingBytes + bytes.LongLength <= 60L * 1024 * 1024, "مجموع صفحات این درس بیش از ۶۰ مگابایت است؛ فایل را به چند درس تقسیم کنید.", 413);
            var totalBytes = await db.Pages.SumAsync(x => (long?)x.Image.Length, ct) ?? 0;
            var maximum = config.GetValue("Cent:MaxContentStorageBytes", 512L * 1024 * 1024);
            CentModule.Check(totalBytes - existingBytes + bytes.LongLength <= maximum, "ظرفیت تعیین‌شده برای محتوای Cent تکمیل شده است. تنظیم ظرفیت را بررسی کنید.", 413);
            if (row is null) { row = new CentDocumentPage { DocumentId = id, Number = page }; db.Pages.Add(row); }
            row.Image = bytes; row.Width = width; row.Height = height; await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        admin.MapPost("/documents/{id:guid}/publish", async (Guid id, PublishInput input, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
            CentModule.Check(document is not null && !document.Published, "درس پیدا نشد یا قبلاً منتشر شده است.", 409);
            CentModule.Check(await db.Pages.CountAsync(x => x.DocumentId == id, ct) == document!.PageCount, "همه صفحات باید پیش از انتشار بارگذاری شوند.", 409);
            CentModule.Check(input.Sections is { Length: <= 200 } && input.Sections.All(s => s.Title is { Length: > 0 and <= 200 } && s.StartPage >= 1 && s.StartPage <= document.PageCount), "فهرست بخش‌ها معتبر نیست.");
            foreach (var section in input.Sections!) db.Sections.Add(new CentSection { DocumentId = id, Title = section.Title.Trim(), StartPage = section.StartPage });
            document.Published = true; Audit(db, p, "PUBLISH_DOCUMENT", id); await db.SaveChangesAsync(ct); return Results.Ok(document);
        });
        admin.MapDelete("/documents/{id:guid}", async (Guid id, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var row = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
            CentModule.Check(row is not null && !row.Published, "فقط پیش‌نویس قابل حذف است.", 409);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Pages.Where(x => x.DocumentId == id).ExecuteDeleteAsync(ct);
            db.Documents.Remove(row!); Audit(db, p, "DELETE_DRAFT", id); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.NoContent();
        });
        admin.MapGet("/users", async (CentDbContext db, CancellationToken ct) => Results.Ok(await db.Users.AsNoTracking()
            .OrderByDescending(u => u.CreatedAt).Select(u => new { u.Id, u.Name, u.Email, u.Role, u.CreatedAt }).Take(500).ToListAsync(ct)));
        admin.MapGet("/orders", async (CentDbContext db, CancellationToken ct) => Results.Ok(await db.Orders.AsNoTracking().OrderByDescending(o => o.CreatedAt).Take(500).ToListAsync(ct)));
        admin.MapPost("/entitlements/{id:guid}/revoke", async (Guid id, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var count = await db.Entitlements.Where(e => e.Id == id && e.RevokedAt == null).ExecuteUpdateAsync(u => u.SetProperty(e => e.RevokedAt, DateTimeOffset.UtcNow), ct);
            CentModule.Check(count == 1, "دسترسی پیدا نشد.", 404); Audit(db, p, "REVOKE_ENTITLEMENT", id); await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    internal static void Audit(CentDbContext db, ClaimsPrincipal p, string action, Guid entityId) =>
        db.Audit.Add(new CentAuditEvent { ActorId = p.UserId(), Action = action, EntityId = entityId });
}
