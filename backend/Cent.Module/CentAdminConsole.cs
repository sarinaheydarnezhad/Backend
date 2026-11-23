using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cent.Module;

public static class CentAdminConsole
{
    public sealed record RoleInput(string Role, bool Confirm);
    private static int Size(int? size) => Math.Clamp(size ?? 20, 1, 50);
    private static int Page(int? page) => Math.Clamp(page ?? 1, 1, 10000);
    internal static async Task<object> List<T>(IQueryable<T> query, int? page, int? size, CancellationToken ct) => new {
        items = await query.Skip((Page(page) - 1) * Size(size)).Take(Size(size)).ToListAsync(ct),
        total = await query.CountAsync(ct), page = Page(page), pageSize = Size(size)
    };
    private static string Search(string? value) => (value ?? "").Trim()[..Math.Min((value ?? "").Trim().Length, 120)];
    public static void MapCentAdminConsole(this RouteGroupBuilder cent)
    {
        var admin = cent.MapGroup("/admin").RequireAuthorization("CentAdmin");
        admin.MapGet("/access", (ClaimsPrincipal p) => Results.Ok(new {
            role = p.FindFirstValue(ClaimTypes.Role), permissions = CentAdminSecurity.Permissions.Where(x => CentAdminSecurity.Allows(p.FindFirstValue(ClaimTypes.Role), x))
        }));
        admin.MapGet("/overview", async (ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new {
                asOf = now, users = await db.Users.CountAsync(ct), resources = await db.Documents.CountAsync(ct),
                publishedResources = await db.Documents.CountAsync(x => x.Published, ct),
                availableAssessments = await db.ExamVersions.CountAsync(x => x.Status == "PUBLISHED" && x.AvailableForNewStarts, ct),
                submittedAttempts = await db.Attempts.CountAsync(x => x.SubmittedAt != null, ct),
                finance = CentAdminSecurity.Allows(p.FindFirstValue(ClaimTypes.Role), "finance") ? new {
                    orders = await db.Orders.CountAsync(ct), paid = await db.Orders.CountAsync(x => x.Status == "PAID", ct),
                    pending = await db.Orders.CountAsync(x => x.Status == "PENDING", ct),
                    incomeRial = await db.Orders.Where(x => x.Status == "PAID" && x.Provider == "ZARINPAL").SumAsync(x => (long?)x.AmountRial, ct) ?? 0,
                    activeSubscriptions = await db.Entitlements.CountAsync(x => x.Kind == "SUBSCRIPTION" && x.RevokedAt == null && x.StartsAt <= now && x.ExpiresAt > now, ct)
                } : null
            });
        });
        admin.MapGet("/users", async (int? page, int? size, string? search, string? role, CentDbContext db, CancellationToken ct) => {
            var q = Search(search); var query = db.Users.AsNoTracking().Where(x => q == "" || x.Name.Contains(q) || x.Email.Contains(q));
            if (!string.IsNullOrEmpty(role)) query = query.Where(x => x.Role == role);
            return Results.Ok(await List(query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                .Select(x => new { x.Id, x.Name, x.Email, x.Role, x.CreatedAt, verifiedIdentity = x.GoogleSubject != null || db.Set<CentMobileIdentity>().Any(i => i.UserId == x.Id) }), page, size, ct));
        }).RequireAuthorization("CentAdmin:users");
        admin.MapGet("/users/{id:guid}", async (Guid id, CentDbContext db, CancellationToken ct) => {
            var user = await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Id, x.Name, x.Email, x.Role, x.CreatedAt, verifiedIdentity = x.GoogleSubject != null || db.Set<CentMobileIdentity>().Any(i => i.UserId == x.Id) }).SingleOrDefaultAsync(ct);
            CentModule.Check(user is not null, "کاربر پیدا نشد.", 404);
            return Results.Ok(new { user, orders = await db.Orders.CountAsync(x => x.UserId == id, ct),
                paidOrders = await db.Orders.CountAsync(x => x.UserId == id && x.Status == "PAID", ct),
                entitlements = await db.Entitlements.AsNoTracking().Where(x => x.UserId == id).OrderByDescending(x => x.StartsAt)
                    .Select(x => new { x.Id, x.Kind, x.StartsAt, x.ExpiresAt, x.RevokedAt }).Take(50).ToListAsync(ct) });
        }).RequireAuthorization("CentAdmin:users");
        admin.MapGet("/roles", () => Results.Ok(new { roles = CentAdminSecurity.Roles.Prepend("STUDENT"),
            matrix = CentAdminSecurity.Roles.Select(role => new { role, permissions = CentAdminSecurity.Permissions.Where(p => CentAdminSecurity.Allows(role, p)) }), legacyAdminIsSuperAdmin = true
        })).RequireAuthorization("CentAdmin:roles");
        admin.MapPut("/users/{id:guid}/role", async (Guid id, RoleInput input, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            CentModule.Check(input.Confirm && (input.Role == "STUDENT" || CentAdminSecurity.Roles.Contains(input.Role)), "نقش معتبر و تأیید صریح لازم است.");
            CentModule.Check(id != p.UserId(), "تغییر نقش حساب خود مجاز نیست.", 409);
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id, ct);
            CentModule.Check(user is not null, "کاربر پیدا نشد.", 404);
            CentModule.Check(input.Role == "STUDENT" || !string.IsNullOrEmpty(user!.GoogleSubject), "برای نقش مدیریتی هویت تأییدشده لازم است.", 409);
            CentModule.Check(user!.Role is not ("ADMIN" or "SuperAdmin") || input.Role == "SuperAdmin" ||
                await db.Users.CountAsync(x => x.Role == "ADMIN" || x.Role == "SuperAdmin", ct) > 1, "آخرین مدیر ارشد قابل تنزل نیست.", 409);
            var previous = user.Role; user.Role = input.Role;
            CentAdminEndpoints.Audit(db, p, $"ROLE_OK:{previous}>{input.Role}", id);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Ok(new { user.Id, user.Role });
        }).RequireAuthorization("CentAdmin:roles");
        admin.MapGet("/orders", async (int? page, int? size, string? search, string? status, CentDbContext db, CancellationToken ct) => {
            var q = Search(search); var query = from o in db.Orders.AsNoTracking() join u in db.Users.AsNoTracking() on o.UserId equals u.Id
                where (q == "" || o.Title.Contains(q) || u.Email.Contains(q) || o.Reference == q) && (status == null || status == "" || o.Status == status)
                orderby o.CreatedAt descending, o.Id select new { o.Id, o.Title, o.Kind, o.AmountRial, o.Status, o.Provider, o.Reference, o.CreatedAt, o.PaidAt, userEmail = u.Email };
            return Results.Ok(await List(query, page, size, ct));
        }).RequireAuthorization("CentAdmin:finance");
        admin.MapGet("/orders/{id:guid}", async (Guid id, CentDbContext db, CancellationToken ct) => {
            var order = await db.Orders.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Id, x.UserId, x.Title, x.Kind, x.AmountRial, x.Status, x.Provider, x.Reference, x.CreatedAt, x.PaidAt, x.DurationDays, x.TrainingId }).SingleOrDefaultAsync(ct);
            CentModule.Check(order is not null, "سفارش پیدا نشد.", 404);
            return Results.Ok(new { order,
                lines = await db.Set<CentSubjectOrderLine>().AsNoTracking().Where(x => x.OrderId == id).ToListAsync(ct),
                entitlements = await db.Entitlements.AsNoTracking().Where(x => x.OrderId == id).ToListAsync(ct),
                subjectAccess = await db.Set<CentSubjectAccess>().AsNoTracking().Where(x => x.OrderId == id).ToListAsync(ct),
                events = await db.PaymentEvents.AsNoTracking().Where(x => x.OrderId == id).OrderByDescending(x => x.CreatedAt).Take(50).Select(x => new { x.Action, x.Code, x.CreatedAt }).ToListAsync(ct),
                recovery = "تأیید و ادامه پرداخت فقط در مسیر امن حساب خریدار انجام می‌شود؛ ثبت دستی پرداخت مجاز نیست."
            });
        }).RequireAuthorization("CentAdmin:finance");
        admin.MapGet("/subscriptions", async (int? page, int? size, string? search, string? status, CentDbContext db, CancellationToken ct) => {
            var q = Search(search); var now = DateTimeOffset.UtcNow;
            var query = from e in db.Entitlements.AsNoTracking() join u in db.Users on e.UserId equals u.Id join o in db.Orders on e.OrderId equals o.Id
                where e.Kind == "SUBSCRIPTION" && (q == "" || u.Email.Contains(q) || o.Title.Contains(q)) &&
                    (status == null || status == "" || status == "active" && e.RevokedAt == null && e.StartsAt <= now && e.ExpiresAt > now ||
                    status == "expired" && e.RevokedAt == null && e.ExpiresAt <= now || status == "revoked" && e.RevokedAt != null || status == "upcoming" && e.RevokedAt == null && e.StartsAt > now)
                orderby e.StartsAt descending, e.Id select new { e.Id, e.UserId, e.OrderId, e.StartsAt, e.ExpiresAt, e.RevokedAt, title = o.Title, userEmail = u.Email };
            return Results.Ok(await List(query, page, size, ct));
        }).RequireAuthorization("CentAdmin:finance");
        admin.MapGet("/products", async (CentDbContext db, CancellationToken ct) => Results.Ok(await db.Products.AsNoTracking().OrderBy(x => x.Title).Take(200).ToListAsync(ct))).RequireAuthorization("CentAdmin:finance");
        admin.MapGet("/product-options", async (CentDbContext db, CancellationToken ct) => Results.Ok(new {
            products = await db.Products.AsNoTracking().OrderBy(x => x.Title).Take(200).ToListAsync(ct),
            trainings = await db.Trainings.AsNoTracking().OrderBy(x => x.Title).Select(x => new { x.Id, x.Title }).Take(200).ToListAsync(ct)
        })).RequireAuthorization("CentAdmin:finance");
        admin.MapGet("/subject-offers", async (CentDbContext db, CancellationToken ct) => Results.Ok(await (
            from offer in db.Set<CentSubjectOffer>().AsNoTracking() join course in db.Courses on offer.SubjectId equals course.Code
            select new { offer.SubjectId, title = course.Title, offer.ContentReleaseId, offer.Available, offer.ReviewedAt, offer.ReviewedBy }
        ).Take(6).ToListAsync(ct))).RequireAuthorization("CentAdmin:finance");
        admin.MapGet("/resources", async (int? page, int? size, string? search, string? status, CentDbContext db, CancellationToken ct) => {
            var q = Search(search); return Results.Ok(await List(db.Documents.AsNoTracking().Where(x => (q == "" || x.Title.Contains(q)) &&
                (status == null || status == "" || status == "published" && x.Published || status == "draft" && !x.Published))
                .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => new { x.Id, x.Title, x.TrainingId, x.PageCount, x.Published, x.SourceId }), page, size, ct));
        }).RequireAuthorization("CentAdmin:content");
        admin.MapGet("/resources/{id:guid}", async (Guid id, CentDbContext db, CancellationToken ct) => {
            var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); CentModule.Check(document is not null, "منبع پیدا نشد.", 404);
            return Results.Ok(new { document, sections = await db.Sections.AsNoTracking().Where(x => x.DocumentId == id).OrderBy(x => x.StartPage).Select(x => new { x.Title, x.StartPage }).Take(200).ToListAsync(ct),
                products = await db.Products.AsNoTracking().Where(x => x.TrainingId == document!.TrainingId).Select(x => new { x.Title, x.Kind, x.Active }).Take(50).ToListAsync(ct),
                grantCount = await db.Entitlements.CountAsync(x => x.TrainingId == document!.TrainingId, ct) });
        }).RequireAuthorization("CentAdmin:content");
        admin.MapGet("/assessments", async (CentDbContext db, CancellationToken ct) => Results.Ok(await db.ExamVersions.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.ConfigurationRevision, x.Status, x.AvailableForNewStarts, x.SyllabusRevisionId, x.ContentReleaseId, x.Revision }).Take(100).ToListAsync(ct))).RequireAuthorization("CentAdmin:exams");
        admin.MapGet("/assessments/{id}", async (string id, CentDbContext db, CancellationToken ct) => {
            var version = await db.ExamVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); CentModule.Check(version is not null, "نسخه آزمون پیدا نشد.", 404);
            return Results.Ok(new { version, attempts = await db.MockAttempts.CountAsync(x => x.ExamVersionId == id, ct) });
        }).RequireAuthorization("CentAdmin:exams");
        admin.MapGet("/question-bank", async (int? page, int? size, string? search, string? status, CentDbContext db, CancellationToken ct) => {
            var q = Search(search); return Results.Ok(await List(db.Questions.AsNoTracking().Where(x => (q == "" || x.Text.Contains(q) || x.Topic.Contains(q)) &&
                (status == null || status == "" || status == "published" && x.Active || status == "draft" && !x.Active)).OrderBy(x => x.Subject).ThenBy(x => x.Id)
                .Select(x => new { x.Id, x.Subject, x.Topic, x.TopicId, x.Difficulty, x.Text, x.OptionsJson, x.CorrectAnswer, x.Explanation, x.Kind, x.Active, managedByRelease = x.ImportKey != null }), page, size, ct));
        }).RequireAuthorization("CentAdmin:exams");
        admin.MapGet("/curriculum", async (CentDbContext db, CancellationToken ct) => Results.Ok(new {
            courses = await db.Courses.AsNoTracking().OrderBy(x => x.SortOrder).ToListAsync(ct), sources = await db.Sources.CountAsync(ct),
            chapters = await db.Chapters.CountAsync(ct), topics = await db.Topics.CountAsync(ct), lessons = await db.SourceSections.CountAsync(ct), exercises = await db.SourceExercises.CountAsync(ct),
            imports = await db.CurriculumImports.AsNoTracking().OrderByDescending(x => x.ImportedAt).Take(10).ToListAsync(ct)
        })).RequireAuthorization("CentAdmin:content");
        admin.MapGet("/reports", async (int? days, CentDbContext db, CancellationToken ct) => {
            var range = Math.Clamp(days ?? 30, 1, 90); var end = DateTimeOffset.UtcNow; var start = new DateTimeOffset(end.UtcDateTime.Date, TimeSpan.Zero).AddDays(1 - range);
            // Aggregate in SQL by calendar fields; never materialize raw historical accounts/orders.
            var registrations = new List<RegistrationDay>();
            if (db.Database.IsSqlServer()) registrations = await db.Users.Where(x => x.CreatedAt >= start && x.CreatedAt <= end)
                .GroupBy(x => new { x.CreatedAt.Year, x.CreatedAt.Month, x.CreatedAt.Day })
                .Select(g => new RegistrationDay(g.Key.Year, g.Key.Month, g.Key.Day, g.Count())).ToListAsync(ct);
            else for (var date = start; date <= end; date = date.AddDays(1)) {
                // SQLite preview stores UTC ticks and cannot translate calendar members.
                // Bounded per-day COUNT fallback; no account records are materialized.
                var next = date.AddDays(1); var count = await db.Users.CountAsync(x => x.CreatedAt >= date && x.CreatedAt < next && x.CreatedAt <= end, ct);
                if (count > 0) registrations.Add(new(date.Year, date.Month, date.Day, count));
            }
            var orders = await db.Orders.Where(x => x.CreatedAt >= start && x.CreatedAt <= end).GroupBy(x => x.Status).Select(g => new { status = g.Key, count = g.Count() }).ToListAsync(ct);
            var attempts = await db.Attempts.Where(x => x.CreatedAt >= start && x.CreatedAt <= end).GroupBy(x => x.Kind).Select(g => new { kind = g.Key, started = g.Count(), submitted = g.Count(x => x.SubmittedAt != null) }).ToListAsync(ct);
            return Results.Ok(new { start, end, days = range, registrations, orders, attempts });
        }).RequireAuthorization("CentAdmin:roles");
        admin.MapGet("/audit", async (int? page, int? size, CentDbContext db, CancellationToken ct) => Results.Ok(await List(db.Audit.AsNoTracking().OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id), page, size, ct))).RequireAuthorization("CentAdmin:roles");
        admin.MapGet("/settings", (IConfiguration config) => Results.Ok(new {
            examsEnabled = config.GetValue<bool>("Cent:Exams:Enabled"),
            googleEnabled = config.GetValue<bool>("Cent:Google:Enabled"),
            subjectMockAdmissionConfigured = config["Cent:Commerce:MockAdmissionPolicy"] == "all-subjects-v1",
            maxContentStorageBytes = config.GetValue("Cent:MaxContentStorageBytes", 512L * 1024 * 1024), roleModel = "single-role-v1"
        })).RequireAuthorization("CentAdmin:settings");
    }
    private sealed record RegistrationDay(int Year, int Month, int Day, int Count);
}
