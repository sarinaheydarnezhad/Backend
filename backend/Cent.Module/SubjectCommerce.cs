using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cent.Module;

public sealed record SubjectQuoteLine(string SubjectId, Guid ProductId, string Title, string ContentReleaseId,
    long ListRial, long DiscountRial, long PayableRial, DateTimeOffset? CurrentAccessEnd);
public sealed record SubjectQuote(Guid Id, string[] SubjectIds, int DistinctCount, long SubtotalRial, int DiscountPercent,
    long DiscountRial, long PayableRial, string Currency, string PricingVersion, string TermPolicy,
    DateTimeOffset ExpiresAt, SubjectQuoteLine[] Lines);
public static class CentSubjectPricing
{
    public const long BaseRial = 4_900_000;
    public const string Version = "subject-irr-2026-v1";
    public const string TermPolicy = "utc-paid-or-contiguous-end-plus-12-calendar-months-v1";
    public static (long Subtotal, int Percent, long Discount, long Payable) Calculate(int count)
    {
        CentModule.Check(count is >= 1 and <= 6, "بین یک تا شش درس انتخاب کن.", 400, "invalid_subject_count");
        var subtotal = checked(BaseRial * count); var percent = (count - 1) * 5;
        var discount = checked(subtotal * percent / 100);
        return (subtotal, percent, discount, subtotal - discount);
    }
}

public sealed class CentSubjectCommerce(CentDbContext db, CentGateway gateway)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<SubjectQuote> Quote(Guid owner, string[] subjects, CancellationToken ct)
    {
        CentModule.Check(subjects is { Length: >= 1 and <= 6 }, "بین یک تا شش درس انتخاب کن.", 400, "invalid_subject_count");
        CentModule.Check(subjects!.Distinct(StringComparer.Ordinal).Count() == subjects.Length, "هر درس فقط یک بار قابل انتخاب است.", 400, "duplicate_subject");
        var catalog = await CentSubjectCatalog.Read(db, ct);
        CentModule.Check(subjects.All(id => catalog.Any(s => s.SubjectId == id)), "درس انتخاب‌شده معتبر نیست.", 400, "unknown_subject");
        var selected = catalog.Where(s => subjects.Contains(s.SubjectId)).OrderBy(s => s.SubjectId, StringComparer.Ordinal).ToArray();
        CentModule.Check(selected.All(s => s.Available), "بعضی درس‌ها هنوز برای خرید آماده نیستند.", 409, "subject_unavailable");
        var offers = await db.Set<CentSubjectOffer>().AsNoTracking().ToDictionaryAsync(o => o.SubjectId, ct);
        var prices = CentSubjectPricing.Calculate(selected.Length);
        var lines = new List<SubjectQuoteLine>();
        foreach (var s in selected) {
            CentModule.Check(!await db.Entitlements.AnyAsync(e => e.UserId == owner && e.Kind == "TRAINING" && e.TrainingId == s.TrainingId && e.RevokedAt == null && e.ExpiresAt == null, ct),
                "این درس دسترسی قبلی بدون تاریخ پایان دارد؛ خرید دوباره ممکن نیست.", 409, "already_owned");
            lines.Add(new(s.SubjectId, offers[s.SubjectId].ProductId, s.Title, s.ContentReleaseId,
                CentSubjectPricing.BaseRial, CentSubjectPricing.BaseRial * prices.Percent / 100,
                CentSubjectPricing.BaseRial * (100 - prices.Percent) / 100, await CurrentEnd(owner, s.SubjectId, ct)));
        }
        var q = new SubjectQuote(Guid.NewGuid(), selected.Select(s => s.SubjectId).ToArray(), selected.Length,
            prices.Subtotal, prices.Percent, prices.Discount, prices.Payable, "IRR", CentSubjectPricing.Version,
            CentSubjectPricing.TermPolicy, DateTimeOffset.UtcNow.AddMinutes(15), lines.ToArray());
        db.Add(new CentSubjectQuote { Id = q.Id, UserId = owner, ExpiresAt = q.ExpiresAt, PayloadJson = JsonSerializer.Serialize(q, Json) });
        await db.SaveChangesAsync(ct); return q;
    }
    private async Task<DateTimeOffset?> CurrentEnd(Guid user, string subject, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await (from a in db.Set<CentSubjectAccess>().AsNoTracking() join e in db.Entitlements on a.EntitlementId equals e.Id
            where a.UserId == user && a.SubjectId == subject && a.RevokedAt == null && e.RevokedAt == null && a.ExpiresAt > now
            select a).ToListAsync(ct);
        // Only contiguous coverage can postpone a renewal; a separated future
        // interval cannot consume time paid for today.
        var end = now;
        foreach (var row in rows.OrderBy(a => a.StartsAt)) if (row.StartsAt <= end && row.ExpiresAt > end) end = row.ExpiresAt;
        return end > now ? end : null;
    }
    public async Task<object> Checkout(Guid owner, Guid quoteId, Guid requestId, bool acceptRenewal, string email, CancellationToken ct)
    {
        CentModule.Check(requestId != Guid.Empty, "شناسه درخواست لازم است.");
        gateway.CheckReady();
        CentOrder order;
        await using (var tx = await db.Database.BeginTransactionAsync(ct)) {
            await Lock(owner, ct);
            var prior = await db.Orders.SingleOrDefaultAsync(o => o.UserId == owner && o.RequestId == requestId, ct);
            var row = await db.Set<CentSubjectQuote>().SingleOrDefaultAsync(q => q.Id == quoteId && q.UserId == owner, ct);
            CentModule.Check(row is not null, "پیش‌فاکتور پیدا نشد.", 404, "quote_not_found");
            if (prior is not null) {
                CentModule.Check(await db.Set<CentSubjectOrder>().AnyAsync(s => s.OrderId == prior.Id && s.QuoteId == quoteId, ct), "شناسه درخواست برای خرید دیگری استفاده شده است.", 409, "request_conflict");
                order = prior;
            } else if (row!.OrderId is { } existingId) order = await db.Orders.SingleAsync(o => o.Id == existingId && o.UserId == owner, ct);
            else {
                CentModule.Check(row!.ExpiresAt > DateTimeOffset.UtcNow, "اعتبار پیش‌فاکتور تمام شده؛ قیمت را دوباره دریافت کن.", 409, "quote_expired");
                var q = JsonSerializer.Deserialize<SubjectQuote>(row.PayloadJson, Json)!;
                var catalog = await CentSubjectCatalog.Read(db, ct);
                CentModule.Check(q.Lines.All(l => catalog.Any(s => s.SubjectId == l.SubjectId && s.Available && s.ContentReleaseId == l.ContentReleaseId)), "وضعیت درس تغییر کرده؛ دوباره انتخاب کن.", 409, "subject_unavailable");
                foreach (var line in q.Lines) {
                    CentModule.Check(await CurrentEnd(owner, line.SubjectId, ct) is null || acceptRenewal, "برای تمدید درس‌های فعال، شرایط تمدید را تأیید کن.", 409, "renewal_confirmation_required");
                    CentModule.Check(!await (from l in db.Set<CentSubjectOrderLine>() join o in db.Orders on l.OrderId equals o.Id
                        where o.UserId == owner && o.Status == "PENDING" && l.SubjectId == line.SubjectId select o).AnyAsync(ct),
                        "برای این درس یک پرداخت در انتظار داری؛ ابتدا همان سفارش را بررسی کن.", 409, "pending_subject_order");
                }
                order = new CentOrder { UserId = owner, ProductId = q.Lines[0].ProductId, RequestId = requestId,
                    Kind = "SUBJECT_ORDER", Title = string.Join("، ", q.Lines.Select(l => l.Title)), AmountRial = q.PayableRial,
                    Provider = gateway.IsMock ? "MOCK" : gateway.Sandbox ? "SANDBOX" : "ZARINPAL" };
                db.Orders.Add(order); row.OrderId = order.Id;
                db.Add(new CentSubjectOrder { OrderId = order.Id, QuoteId = q.Id, PayloadJson = row.PayloadJson });
                foreach (var l in q.Lines) db.Add(new CentSubjectOrderLine { OrderId = order.Id, SubjectId = l.SubjectId,
                    ProductId = l.ProductId, ContentReleaseId = l.ContentReleaseId, ListRial = l.ListRial, DiscountRial = l.DiscountRial, PayableRial = l.PayableRial });
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        return await Initiate(owner, order.Id, email, ct);
    }
    public async Task<object> Initiate(Guid owner, Guid orderId, string email, CancellationToken ct)
    {
        CentOrder order;
        await using (var tx = await db.Database.BeginTransactionAsync(ct)) {
            await Lock(owner, ct);
            order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId && o.UserId == owner, ct)
                ?? throw new CentException(404, "order_not_found", "سفارش پیدا نشد.");
            var companion = await db.Set<CentSubjectOrder>().SingleAsync(s => s.OrderId == orderId, ct);
            if (order.Authority is null && order.Status == "PENDING") {
                gateway.CheckReady(order.Provider);
                CentModule.Check(companion.InitiationState == "NOT_STARTED", "نتیجه اتصال قبلی به درگاه مشخص نیست؛ این سفارش نیاز به بررسی پشتیبانی دارد.", 409, "initiation_uncertain");
                companion.InitiationState = "REQUESTING";
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        if (order.Authority is null && order.Status == "PENDING") {
            // REQUESTING commits before the external side effect. A crash or
            // uncertain response cannot silently create a second authority.
            try {
                var authority = await gateway.Request(order, email, ct);
                await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Authority, authority), ct);
                await db.Set<CentSubjectOrder>().Where(s => s.OrderId == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.InitiationState, "READY"), ct);
                db.PaymentEvents.Add(new() { OrderId = order.Id, Action = "REQUEST", Code = 100 }); await db.SaveChangesAsync(ct);
                order.Authority = authority;
            } catch (CentException e) when (e.Code == "gateway_rejected") {
                await db.Set<CentSubjectOrder>().Where(s => s.OrderId == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.InitiationState, "NOT_STARTED"), ct); throw;
            }
        }
        return new { order.Id, order.Status, order.AmountRial, redirectUrl = order.Status != "PENDING" ? gateway.ResultUrl(order.Id) :
            order.Provider == "MOCK" ? gateway.DevelopmentUrl(order.Id) : gateway.CheckoutUrl(order) };
    }
    internal Task<int> Lock(Guid owner, CancellationToken ct) => db.Users.Where(u => u.Id == owner)
        .ExecuteUpdateAsync(u => u.SetProperty(x => x.PurchaseVersion, x => x.PurchaseVersion + 1), ct);
    // Called inside the existing CentPayments transaction and user lock.
    internal async Task Grant(CentOrder order, DateTimeOffset paidAt, CancellationToken ct)
    {
        var snapshot = await db.Set<CentSubjectOrder>().AsNoTracking().SingleAsync(o => o.OrderId == order.Id, ct);
        var q = JsonSerializer.Deserialize<SubjectQuote>(snapshot.PayloadJson, Json)!;
        var lines = await db.Set<CentSubjectOrderLine>().Where(l => l.OrderId == order.Id).ToListAsync(ct);
        CentModule.Check(q.Currency == "IRR" && q.PricingVersion == CentSubjectPricing.Version && q.TermPolicy == CentSubjectPricing.TermPolicy &&
            q.PayableRial == order.AmountRial && lines.Count == q.DistinctCount && lines.Sum(l => l.PayableRial) == order.AmountRial &&
            q.Lines.All(l => lines.Any(s => s.SubjectId == l.SubjectId && s.ProductId == l.ProductId && s.PayableRial == l.PayableRial && s.ContentReleaseId == l.ContentReleaseId)),
            "اطلاعات سفارش نیاز به بررسی دارد.", 409, "order_integrity_error");
        var parent = new CentEntitlement { UserId = order.UserId, OrderId = order.Id, Kind = "SUBJECT_ORDER", StartsAt = paidAt };
        db.Entitlements.Add(parent);
        foreach (var line in lines) {
            var end = await CurrentEnd(order.UserId, line.SubjectId, ct); var starts = end > paidAt ? end.Value : paidAt;
            db.Add(new CentSubjectAccess { EntitlementId = parent.Id, OrderId = order.Id, UserId = order.UserId,
                SubjectId = line.SubjectId, ContentReleaseId = line.ContentReleaseId, StartsAt = starts, ExpiresAt = starts.AddMonths(12) });
        }
    }
}

public static class CentSubjectEndpoints
{
    public sealed record QuoteInput(string[] SubjectIds);
    public sealed record CheckoutInput(Guid QuoteId, Guid RequestId, bool AcceptRenewal = false);
    public sealed record DevelopmentInput(string Outcome);
    public sealed record OfferApproval(string ContentReleaseId, bool Available, bool ConfirmReviewedContent);
    public sealed record BundleInput(string Id, string Title, string[] SubjectIds, string Rationale, string Evidence, bool Active);
    public static void MapCentSubjects(this RouteGroupBuilder group)
    {
        group.MapPost("/admin/subject-offers/prepare", async (ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            await CentSubjectCatalog.PrepareOffers(db, ct);
            CentAdminEndpoints.Audit(db, p, "PREPARE_SUBJECT_OFFERS", Guid.Empty); await db.SaveChangesAsync(ct);
            return Results.Ok(await CentSubjectCatalog.Read(db, ct));
        }).RequireAuthorization("CentAdmin:finance");
        group.MapPut("/admin/subject-offers/{subjectId}", async (string subjectId, OfferApproval input, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var offer = await db.Set<CentSubjectOffer>().SingleOrDefaultAsync(o => o.SubjectId == subjectId, ct);
            CentModule.Check(offer is not null, "محصول درسی پیدا نشد.", 404);
            CentModule.Check(input.ContentReleaseId == offer!.ContentReleaseId, "نسخهٔ محتوا تغییر کرده است.", 409);
            CentModule.Check(!input.Available || input.ConfirmReviewedContent, "تأیید بازبینی محتوای قابل فروش لازم است.", 409, "review_confirmation_required");
            offer.Available = input.Available;
            if (input.Available) { offer.ReviewedAt = DateTimeOffset.UtcNow; offer.ReviewedBy = p.FindFirstValue(ClaimTypes.Email) ?? p.UserId().ToString(); }
            (await db.Products.SingleAsync(product => product.Id == offer.ProductId, ct)).Active = input.Available;
            CentAdminEndpoints.Audit(db, p, input.Available ? "APPROVE_SUBJECT_OFFER" : "DISABLE_SUBJECT_OFFER", offer.ProductId);
            await db.SaveChangesAsync(ct); return Results.Ok(offer);
        }).RequireAuthorization("CentAdmin:finance");
        group.MapPut("/admin/recommended-bundles/{id}", async (string id, BundleInput input, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            CentModule.Check(id == input.Id && id.Length is > 0 and <= 100 && input.Title.Length is > 0 and <= 200 &&
                input.SubjectIds is { Length: >= 1 and <= 6 } && input.SubjectIds.Distinct().Count() == input.SubjectIds.Length &&
                input.SubjectIds.All(s => Curriculum.CurriculumIdentity.Courses.Any(c => c.Code == s)) &&
                !string.IsNullOrWhiteSpace(input.Rationale) && !string.IsNullOrWhiteSpace(input.Evidence), "اطلاعات بسته یا مستندات ارتباط آموزشی کامل نیست.");
            var row = await db.Set<CentRecommendedBundle>().FindAsync([id], ct);
            if (row is null) { row = new() { Id = id }; db.Add(row); }
            row.Title = input.Title; row.SubjectsJson = JsonSerializer.Serialize(input.SubjectIds); row.Rationale = input.Rationale; row.Evidence = input.Evidence; row.Active = input.Active;
            CentAdminEndpoints.Audit(db, p, "UPDATE_RECOMMENDED_BUNDLE", Guid.Empty); await db.SaveChangesAsync(ct); return Results.Ok(row);
        }).RequireAuthorization("CentAdmin:finance");
        group.MapGet("/subject-quotes/{id:guid}", async (Guid id, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var quote = await db.Set<CentSubjectQuote>().AsNoTracking().SingleOrDefaultAsync(q => q.Id == id && q.UserId == p.UserId(), ct);
            CentModule.Check(quote is not null, "پیش‌فاکتور پیدا نشد.", 404);
            return Results.Content(quote!.PayloadJson, "application/json");
        }).RequireAuthorization("CentUser");
        group.MapGet("/me/orders/{id:guid}", async (Guid id, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id && o.UserId == p.UserId(), ct);
            CentModule.Check(order is not null, "سفارش پیدا نشد.", 404);
            var snapshot = await db.Set<CentSubjectOrder>().AsNoTracking().SingleOrDefaultAsync(o => o.OrderId == id, ct);
            return Results.Ok(new { order, quote = snapshot is null ? null : JsonSerializer.Deserialize<SubjectQuote>(snapshot.PayloadJson, CentSubjectCommerce.Json),
                access = await db.Set<CentSubjectAccess>().AsNoTracking().Where(a => a.OrderId == id && a.UserId == p.UserId()).ToListAsync(ct) });
        }).RequireAuthorization("CentUser");
        group.MapPost("/subject-quotes", async (QuoteInput input, ClaimsPrincipal p, CentSubjectCommerce service, CancellationToken ct) =>
            Results.Ok(await service.Quote(p.UserId(), input.SubjectIds, ct))).RequireAuthorization("CentUser");
        group.MapPost("/subject-checkout", async (CheckoutInput input, ClaimsPrincipal p, CentSubjectCommerce service, CancellationToken ct) =>
            Results.Ok(await service.Checkout(p.UserId(), input.QuoteId, input.RequestId, input.AcceptRenewal, p.FindFirstValue(ClaimTypes.Email)!, ct))).RequireAuthorization("CentUser");
        group.MapPost("/me/orders/{id:guid}/resume", async (Guid id, ClaimsPrincipal p, CentSubjectCommerce s, CancellationToken ct) =>
            Results.Ok(await s.Initiate(p.UserId(), id, p.FindFirstValue(ClaimTypes.Email)!, ct))).RequireAuthorization("CentUser");
        group.MapGet("/me/subject-access", async (ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => Results.Ok(
            await (from a in db.Set<CentSubjectAccess>().AsNoTracking() join e in db.Entitlements on a.EntitlementId equals e.Id
                where a.UserId == p.UserId() select new { a.Id, a.SubjectId, a.OrderId, a.StartsAt, a.ExpiresAt, revokedAt = a.RevokedAt ?? e.RevokedAt }).ToListAsync(ct))).RequireAuthorization("CentUser");
        group.MapPost("/development/payments/{id:guid}", async (Guid id, DevelopmentInput input, ClaimsPrincipal p, CentDbContext db, CentGateway gateway, CentPayments payments, CancellationToken ct) => {
            CentModule.Check(gateway.IsMock, "این مسیر فقط برای پرداخت آزمایشی محلی است.", 404);
            var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id && o.UserId == p.UserId() && o.Provider == "MOCK", ct);
            CentModule.Check(order is not null && order.Authority is not null && order.Kind == "SUBJECT_ORDER", "سفارش پیدا نشد.", 404);
            CentModule.Check(input.Outcome is "success" or "failed" or "cancelled", "نتیجه معتبر نیست.");
            if (input.Outcome == "success") {
                await db.Set<CentSubjectOrder>().Where(o => o.OrderId == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.DevelopmentOutcome, "success"), ct);
                await payments.Confirm(id, ct);
            } else {
                await db.Orders.Where(o => o.Id == id && o.Status == "PENDING").ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, input.Outcome == "failed" ? "FAILED" : "CANCELLED"), ct);
            }
            return Results.Ok(new { redirectUrl = gateway.ResultUrl(id) });
        }).RequireAuthorization("CentUser");
    }
}
