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
    public string ResultUrl(Guid orderId) => BaseUrl("Cent:ClientBaseUrl") + "/checkout/success?order=" + orderId;
    public string DevelopmentUrl(Guid orderId) => BaseUrl("Cent:ClientBaseUrl") + "/checkout/development?order=" + orderId;
    private static string OrderHost(CentOrder order) => order.Provider switch {
        "SANDBOX" => "https://sandbox.zarinpal.com",
        "ZARINPAL" => "https://payment.zarinpal.com",
        _ => throw new CentException(409, "invalid_payment_provider", "نوع درگاه سفارش معتبر نیست.")
    };
    public string CheckoutUrl(CentOrder order) => OrderHost(order) + "/pg/StartPay/" + Uri.EscapeDataString(order.Authority!);
    private string BaseUrl(string key)
    {
        var value = config[key]?.TrimEnd('/') ?? "";
        CentModule.Check(Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "https" || (environment.IsDevelopment() && uri.IsLoopback && uri.Scheme == "http")) &&
            string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment), "آدرس سرویس پرداخت هنوز توسط مدیر تنظیم نشده است.", 503, "payments_not_configured");
        return value;
    }
    private void RequireConfigured(string provider)
    {
        _ = BaseUrl("Cent:ClientBaseUrl"); _ = BaseUrl("Cent:PublicApiBaseUrl");
        if (provider == "MOCK") {
            CentModule.Check(IsMock, "پرداخت آزمایشی در این محیط قابل تأیید نیست.", 403, "mock_payment_disabled");
        } else {
            _ = OrderHost(new CentOrder { Provider = provider });
            CentModule.Check(Guid.TryParse(config["Cent:Payments:MerchantId"], out var merchant) && merchant != Guid.Empty,
                "درگاه پرداخت هنوز فعال نشده است.", 503, "payments_not_configured");
        }
    }
    public void CheckReady(string? provider = null) => RequireConfigured(provider ?? (IsMock ? "MOCK" : Sandbox ? "SANDBOX" : "ZARINPAL"));

    private static CentException InvalidResponse() => new(502, "gateway_invalid_response", "پاسخ درگاه معتبر نیست. دوباره وضعیت سفارش را بررسی کنید.");
    private static bool TryCode(JsonElement payload, string property, out JsonElement data, out int code)
    {
        data = default; code = 0;
        return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out data) &&
            data.ValueKind == JsonValueKind.Object && data.TryGetProperty("code", out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out code);
    }
    private static async Task<JsonElement> ReadResponse(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        try { return await response.Content.ReadFromJsonAsync<JsonElement>(ct); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    public async Task<string> Request(CentOrder order, string email, CancellationToken ct)
    {
        RequireConfigured(order.Provider);
        if (order.Provider == "MOCK") return "CENT-DEV-" + order.Id.ToString("N");
        using var response = await http.PostAsJsonAsync(OrderHost(order) + "/pg/v4/payment/request.json", new {
            merchant_id = config["Cent:Payments:MerchantId"], amount = order.AmountRial, currency = "IRR",
            description = order.Title, callback_url = BaseUrl("Cent:PublicApiBaseUrl") + "/api/cent/v1/payments/callback",
            metadata = new { email, order_id = order.Id }
        }, ct);
        var payload = await ReadResponse(response, ct);
        if (!TryCode(payload, "data", out var data, out var code)) {
            if (TryCode(payload, "errors", out _, out _)) throw new CentException(502, "gateway_rejected", "درگاه درخواست پرداخت را نپذیرفت.");
            throw InvalidResponse();
        }
        CentModule.Check(code == 100,
            "درگاه درخواست پرداخت را نپذیرفت. دوباره تلاش کنید.", 502, "gateway_rejected");
        if (!data.TryGetProperty("authority", out var value) || value.ValueKind != JsonValueKind.String) throw InvalidResponse();
        var authority = value.GetString() ?? "";
        if (!(authority.Length is >= 10 and <= 100 && authority.All(char.IsAsciiLetterOrDigit))) throw InvalidResponse();
        return authority;
    }

    public async Task<(int Code, string? Reference)> Verify(CentOrder order, CancellationToken ct)
    {
        RequireConfigured(order.Provider);
        if (order.Provider == "MOCK") {
            return (100, "DEV-" + order.Id.ToString("N"));
        }
        using var response = await http.PostAsJsonAsync(OrderHost(order) + "/pg/v4/payment/verify.json", new {
            merchant_id = config["Cent:Payments:MerchantId"], amount = order.AmountRial, authority = order.Authority
        }, ct);
        var payload = await ReadResponse(response, ct);
        if (!TryCode(payload, "data", out var data, out var code)) {
            if (TryCode(payload, "errors", out _, out var errorCode) && errorCode < 0) return (errorCode, null);
            throw InvalidResponse();
        }
        if (code is not (100 or 101)) return (code, null);
        if (!data.TryGetProperty("ref_id", out var referenceValue)) throw InvalidResponse();
        // Zarinpal's reference is a positive numeric identifier, never an arbitrary JSON object.
        var reference = referenceValue.ValueKind is JsonValueKind.Number or JsonValueKind.String ? referenceValue.ToString() : "";
        if (reference.Length is < 1 or > 100 || !reference.All(char.IsAsciiDigit) || !reference.Any(c => c != '0')) throw InvalidResponse();
        return (code, reference);
    }
}

public sealed class CentPayments(CentDbContext db, CentGateway gateway)
{
    public async Task<object> Checkout(Guid userId, Guid productId, Guid requestId, string email, CancellationToken ct)
    {
        CentModule.Check(requestId != Guid.Empty, "شناسه درخواست لازم است.");
        var existing = await db.Orders.SingleOrDefaultAsync(o => o.UserId == userId && o.RequestId == requestId, ct);
        CentModule.Check(existing is null || existing.ProductId == productId, "این درخواست برای محصول دیگری استفاده شده است.", 409);
        var order = existing;
        if (order is null) {
            gateway.CheckReady();
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId && p.Active, ct);
            CentModule.Check(product is not null, "محصول در دسترس نیست.", 404);
            CentModule.Check(product!.Kind is "TRAINING" or "SUBSCRIPTION", "برای خرید درس از پیش‌فاکتور استفاده کن.", 409, "subject_quote_required");
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
        if (order.Status == "PAID") return new { order.Id, redirectUrl = gateway.ResultUrl(order.Id), order.Status, order.AmountRial };
        gateway.CheckReady(order.Provider);
        // The same database lock used by completion also serializes authority creation across host instances.
        // Keep the snapshot persisted if the provider fails; retry with the same RequestId.
        await using (var transaction = await db.Database.BeginTransactionAsync(ct)) {
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.PurchaseVersion, x => x.PurchaseVersion + 1), ct);
            await db.Entry(order).ReloadAsync(ct);
            if (order.Status != "PAID" && order.Authority is null) {
                CentModule.Check(await db.Products.AnyAsync(p => p.Id == productId && p.Active, ct), "محصول در دسترس نیست.", 404);
                if (order.Kind == "TRAINING") {
                    CentModule.Check(await db.Trainings.AnyAsync(t => t.Id == order.TrainingId && t.Published, ct), "این آموزش منتشر نشده است.", 404);
                    CentModule.Check(!await db.Entitlements.AnyAsync(e => e.UserId == userId && e.Kind == "TRAINING" && e.TrainingId == order.TrainingId && e.RevokedAt == null, ct),
                        "این آموزش قبلاً خریداری شده است.", 409, "already_owned");
                }
                var authority = await gateway.Request(order, email, ct);
                order.Authority = authority;
                db.PaymentEvents.Add(new CentPaymentEvent { OrderId = order.Id, Action = "REQUEST", Code = 100 });
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        if (order.Status == "PAID") return new { order.Id, redirectUrl = gateway.ResultUrl(order.Id), order.Status, order.AmountRial };
        if (order.Provider == "MOCK") {
            await Confirm(order.Id, ct);
            order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id, ct);
        }
        return new { order.Id, redirectUrl = order.Provider == "MOCK" ? gateway.ResultUrl(order.Id) : gateway.CheckoutUrl(order), order.Status, order.AmountRial };
    }

    public async Task<bool> Confirm(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, ct);
        if (order.Status == "PAID") return true;
        CentModule.Check(order.Authority is not null, "درخواست پرداخت هنوز آماده نیست.", 409);
        if (order.Kind == "SUBJECT_ORDER" && order.Provider == "MOCK") {
            CentModule.Check(gateway.IsMock, "پرداخت آزمایشی غیرفعال است.", 403, "mock_payment_disabled");
            if (!await db.Set<CentSubjectOrder>().AnyAsync(s => s.OrderId == order.Id && s.DevelopmentOutcome == "success", ct)) return false;
        }
        var verified = await gateway.Verify(order, ct);
        db.PaymentEvents.Add(new CentPaymentEvent { OrderId = order.Id, Action = "VERIFY", Code = verified.Code });
        if (verified.Code is not (100 or 101) || string.IsNullOrWhiteSpace(verified.Reference) || verified.Reference == "0") {
            if (order.Kind == "SUBJECT_ORDER") await db.Orders.Where(o => o.Id == order.Id && o.Status != "PAID").ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "FAILED"), ct);
            await db.SaveChangesAsync(ct); return false;
        }
        // Serialize purchases per Cent user. Grant and payment state commit atomically; retries cannot double-extend a subscription.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Users.Where(u => u.Id == order.UserId).ExecuteUpdateAsync(u => u.SetProperty(x => x.PurchaseVersion, x => x.PurchaseVersion + 1), ct);
        var fresh = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, ct);
        if (fresh.Status == "PAID") { await transaction.CommitAsync(ct); db.ChangeTracker.Clear(); return true; }
        CentModule.Check(!await db.Orders.AsNoTracking().AnyAsync(o => o.Id != orderId && o.Provider == order.Provider && o.Reference == verified.Reference, ct),
            "این کد پیگیری قبلاً برای سفارش دیگری ثبت شده است؛ وضعیت پرداخت نیاز به بررسی دارد.", 409, "payment_reference_reused");
        var now = DateTimeOffset.UtcNow;
        var starts = now;
        if (order.Kind == "SUBSCRIPTION") {
            var currentEnd = await db.Entitlements.Where(e => e.UserId == order.UserId && e.Kind == "SUBSCRIPTION" && e.RevokedAt == null)
                .MaxAsync(e => e.ExpiresAt, ct);
            if (currentEnd > starts) starts = currentEnd.Value;
        }
        await db.Orders.Where(o => o.Id == orderId).ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, "PAID")
            .SetProperty(o => o.PaidAt, now).SetProperty(o => o.Reference, verified.Reference), ct);
        if (order.Kind == "SUBJECT_ORDER") await new CentSubjectCommerce(db, gateway).Grant(order, now, ct);
        else {
            CentModule.Check(order.Kind is "TRAINING" or "SUBSCRIPTION", "نوع سفارش معتبر نیست.", 409, "order_integrity_error");
            db.Entitlements.Add(new CentEntitlement { UserId = order.UserId, OrderId = order.Id, TrainingId = order.TrainingId,
                Kind = order.Kind, StartsAt = starts, ExpiresAt = order.Kind == "SUBSCRIPTION" ? starts.AddDays(order.DurationDays!.Value) : null });
        }
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
                .Select(o => new { o.Id, o.Title, o.Kind, o.AmountRial, o.Status, o.Provider, o.Reference, o.CreatedAt, o.PaidAt,
                    o.ProductId, o.RequestId, o.TrainingId, o.DurationDays }).Take(200).ToListAsync(ct)))
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
