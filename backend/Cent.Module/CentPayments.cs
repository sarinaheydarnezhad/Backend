using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Cent.Module;

public sealed class CentGateway(HttpClient http, IConfiguration config, IHostEnvironment environment)
{
    public bool IsMock => environment.IsDevelopment() && config.GetValue<bool>("Cent:Payments:AllowDevelopmentMock");
    public bool Sandbox => config.GetValue("Cent:Payments:Sandbox", true);
    public string Mode => IsMock ? "DEVELOPMENT_MOCK" : string.IsNullOrWhiteSpace(config["Cent:Payments:MerchantId"]) ? "NOT_CONFIGURED" : Sandbox ? "SANDBOX" : "LIVE";
    private string Host => Sandbox ? "https://sandbox.zarinpal.com" : "https://payment.zarinpal.com";
    public string ResultUrl(Guid orderId) => BaseUrl("Cent:ClientBaseUrl") + "/checkout/success?order=" + orderId;
    private static string OrderHost(CentOrder order) => order.Provider == "SANDBOX" ? "https://sandbox.zarinpal.com" : "https://payment.zarinpal.com";
    public string CheckoutUrl(CentOrder order) => OrderHost(order) + "/pg/StartPay/" + Uri.EscapeDataString(order.Authority!);
    private string BaseUrl(string key)
    {
        var value = config[key]?.TrimEnd('/') ?? "";
        CentModule.Check(Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "https" || (environment.IsDevelopment() && uri.IsLoopback && uri.Scheme == "http")) &&
            string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment), "آدرس سرویس پرداخت هنوز توسط مدیر تنظیم نشده است.", 503, "payments_not_configured");
        return value;
    }
    private void RequireConfigured()
    {
        _ = BaseUrl("Cent:ClientBaseUrl"); _ = BaseUrl("Cent:PublicApiBaseUrl");
        CentModule.Check(IsMock || Guid.TryParse(config["Cent:Payments:MerchantId"], out _), "درگاه پرداخت هنوز فعال نشده است.", 503, "payments_not_configured");
    }
    public void CheckReady() => RequireConfigured();

    public async Task<string> Request(CentOrder order, string email, CancellationToken ct)
    {
        RequireConfigured();
        if (IsMock) return "CENT-DEV-" + order.Id.ToString("N");
        using var response = await http.PostAsJsonAsync(OrderHost(order) + "/pg/v4/payment/request.json", new {
            merchant_id = config["Cent:Payments:MerchantId"], amount = order.AmountRial, currency = "IRR",
            description = order.Title, callback_url = BaseUrl("Cent:PublicApiBaseUrl") + "/api/cent/v1/payments/callback",
            metadata = new { email, order_id = order.Id }
        }, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        CentModule.Check(payload.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("code", out var code) && code.GetInt32() == 100 && data.TryGetProperty("authority", out _),
            "درگاه درخواست پرداخت را نپذیرفت. دوباره تلاش کنید.", 502, "gateway_rejected");
        var authority = data.GetProperty("authority").GetString() ?? "";
        CentModule.Check(authority.Length is >= 10 and <= 100 && authority.All(char.IsAsciiLetterOrDigit), "پاسخ درگاه معتبر نیست.", 502);
        return authority;
    }

    public async Task<(int Code, string? Reference)> Verify(CentOrder order, CancellationToken ct)
    {
        RequireConfigured();
        if (order.Provider == "MOCK") {
            CentModule.Check(IsMock, "پرداخت آزمایشی در این محیط قابل تأیید نیست.", 403);
            return (100, "DEV-" + order.Id.ToString("N"));
        }
        using var response = await http.PostAsJsonAsync(OrderHost(order) + "/pg/v4/payment/verify.json", new {
            merchant_id = config["Cent:Payments:MerchantId"], amount = order.AmountRial, authority = order.Authority
        }, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("code", out var code)) return (-1, null);
        var resultCode = code.GetInt32();
        var reference = data.TryGetProperty("ref_id", out var referenceValue) ? referenceValue.ToString() : null;
        return (resultCode, reference);
    }
}

public sealed class CentPayments(CentDbContext db, CentGateway gateway)
{
    public async Task<object> Checkout(Guid userId, Guid productId, Guid requestId, string email, CancellationToken ct)
    {
        CentModule.Check(requestId != Guid.Empty, "شناسه درخواست لازم است.");
        gateway.CheckReady();
        var existing = await db.Orders.SingleOrDefaultAsync(o => o.UserId == userId && o.RequestId == requestId, ct);
        CentModule.Check(existing is null || existing.ProductId == productId, "این درخواست برای محصول دیگری استفاده شده است.", 409);
        var order = existing;
        if (order is null) {
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId && p.Active, ct);
            CentModule.Check(product is not null, "محصول در دسترس نیست.", 404);
            if (product!.Kind == "TRAINING") {
                CentModule.Check(await db.Trainings.AnyAsync(t => t.Id == product.TrainingId && t.Published, ct), "این آموزش منتشر نشده است.", 404);
                CentModule.Check(!await db.Entitlements.AnyAsync(e => e.UserId == userId && e.Kind == "TRAINING" && e.TrainingId == product.TrainingId && e.RevokedAt == null, ct),
                    "این آموزش قبلاً خریداری شده است.", 409, "already_owned");
            }
            order = new CentOrder { UserId = userId, ProductId = productId, RequestId = requestId, Title = product.Title,
                Kind = product.Kind, TrainingId = product.TrainingId, DurationDays = product.DurationDays,
                AmountRial = product.PriceRial, Provider = gateway.IsMock ? "MOCK" : gateway.Sandbox ? "SANDBOX" : "ZARINPAL" };
            db.Orders.Add(order);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) {
                db.ChangeTracker.Clear();
                order = await db.Orders.SingleOrDefaultAsync(o => o.UserId == userId && o.RequestId == requestId, ct);
                if (order is null) throw;
                CentModule.Check(order.ProductId == productId, "شناسه درخواست تکراری است.", 409);
            }
        }
        if (order.Status == "PAID") return new { order.Id, redirectUrl = gateway.ResultUrl(order.Id), order.Status };
        if (order.Authority is null) {
            var authority = await gateway.Request(order, email, ct);
            // Only one concurrent checkout request wins; every retry returns the stored authority.
            await db.Orders.Where(o => o.Id == order.Id && o.Authority == null)
                .ExecuteUpdateAsync(u => u.SetProperty(o => o.Authority, authority), ct);
            await db.Entry(order).ReloadAsync(ct);
            db.PaymentEvents.Add(new CentPaymentEvent { OrderId = order.Id, Action = "REQUEST", Code = 100 });
            await db.SaveChangesAsync(ct);
        }
        if (order.Provider == "MOCK") await Confirm(order.Id, ct);
        return new { order.Id, redirectUrl = order.Provider == "MOCK" ? gateway.ResultUrl(order.Id) : gateway.CheckoutUrl(order), order.Status };
    }

    public async Task<bool> Confirm(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, ct);
        if (order.Status == "PAID") return true;
        CentModule.Check(order.Authority is not null, "درخواست پرداخت هنوز آماده نیست.", 409);
        var verified = await gateway.Verify(order, ct);
        db.PaymentEvents.Add(new CentPaymentEvent { OrderId = order.Id, Action = "VERIFY", Code = verified.Code });
        if (verified.Code is not (100 or 101) || string.IsNullOrWhiteSpace(verified.Reference) || verified.Reference == "0") {
            await db.SaveChangesAsync(ct); return false;
        }
        // Serialize purchases per Cent user. Grant and payment state commit atomically; retries cannot double-extend a subscription.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Users.Where(u => u.Id == order.UserId).ExecuteUpdateAsync(u => u.SetProperty(x => x.PurchaseVersion, x => x.PurchaseVersion + 1), ct);
        var fresh = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, ct);
        if (fresh.Status == "PAID") { await transaction.CommitAsync(ct); db.ChangeTracker.Clear(); return true; }
        var now = DateTimeOffset.UtcNow;
        var starts = now;
        if (order.Kind == "SUBSCRIPTION") {
            var currentEnd = await db.Entitlements.Where(e => e.UserId == order.UserId && e.Kind == "SUBSCRIPTION" && e.RevokedAt == null)
                .MaxAsync(e => e.ExpiresAt, ct);
            if (currentEnd > starts) starts = currentEnd.Value;
        }
        await db.Orders.Where(o => o.Id == orderId).ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, "PAID")
            .SetProperty(o => o.PaidAt, now).SetProperty(o => o.Reference, verified.Reference), ct);
        db.Entitlements.Add(new CentEntitlement { UserId = order.UserId, OrderId = order.Id, TrainingId = order.TrainingId,
            Kind = order.Kind, StartsAt = starts, ExpiresAt = order.Kind == "SUBSCRIPTION" ? starts.AddDays(order.DurationDays!.Value) : null });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); db.ChangeTracker.Clear(); return true;
    }
}

public static class CentCommerceEndpoints
{
    public sealed record CheckoutInput(Guid ProductId, Guid RequestId);
    public static void MapCentCommerce(this RouteGroupBuilder group)
    {
        group.MapPost("/checkout", async (CheckoutInput input, ClaimsPrincipal p, CentPayments service, CancellationToken ct) =>
            Results.Ok(await service.Checkout(p.UserId(), input.ProductId, input.RequestId, p.FindFirstValue(ClaimTypes.Email)!, ct)))
            .RequireAuthorization("CentUser");
        group.MapGet("/me/orders", async (ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => Results.Ok(
            await db.Orders.AsNoTracking().Where(o => o.UserId == p.UserId()).OrderByDescending(o => o.CreatedAt)
                .Select(o => new { o.Id, o.Title, o.Kind, o.AmountRial, o.Status, o.Provider, o.Reference, o.CreatedAt, o.PaidAt }).Take(200).ToListAsync(ct)))
            .RequireAuthorization("CentUser");
        group.MapPost("/me/orders/{id:guid}/verify", async (Guid id, ClaimsPrincipal p, CentDbContext db, CentPayments service, CancellationToken ct) => {
            var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id && o.UserId == p.UserId(), ct);
            CentModule.Check(order is not null, "سفارش پیدا نشد.", 404);
            var paid = order!.Status == "PAID" || (order.Authority is not null && await service.Confirm(id, ct));
            return Results.Ok(new { paid });
        }).RequireAuthorization("CentUser");
        group.MapGet("/payments/callback", async (HttpContext http, CentDbContext db, CentPayments service, CentGateway gateway, CancellationToken ct) => {
            var authority = http.Request.Query["Authority"].ToString();
            CentModule.Check(authority.Length is > 0 and <= 100, "شناسه پرداخت معتبر نیست.");
            var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Authority == authority, ct);
            CentModule.Check(order is not null, "سفارش پیدا نشد.", 404);
            if (http.Request.Query["Status"] == "OK") await service.Confirm(order!.Id, ct);
            return Results.Redirect(gateway.ResultUrl(order!.Id));
        });
    }
}
