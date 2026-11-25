using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cent.Module;

public static class CentIranPhone
{
    public static string Normalize(string? input)
    {
        var phone = string.Concat((input ?? "").Trim().Select(c => c is >= '۰' and <= '۹' ? (char)('0' + c - '۰') : c is >= '٠' and <= '٩' ? (char)('0' + c - '٠') : c));
        if (Regex.IsMatch(phone, "\\A09[0-9]{9}\\z")) phone = "+98" + phone[1..];
        else if (Regex.IsMatch(phone, "\\A989[0-9]{9}\\z")) phone = "+" + phone;
        CentModule.Check(Regex.IsMatch(phone, "\\A\\+989[0-9]{9}\\z"), "شماره موبایل معتبر ایران وارد کنید.", 400, "invalid_phone");
        return phone;
    }
}

public sealed record CentSmsRequest(string Phone)
{
    public override string ToString() => "Cent SMS request (redacted)";
}
public sealed record CentSmsVerify(string ChallengeId, string Code, string? Password = null)
{
    public override string ToString() => "Cent SMS verification (redacted)";
}
public sealed record CentSmsRequested(string ChallengeId, string Phone, DateTimeOffset ExpiresAt, DateTimeOffset ResendAt, DateTimeOffset ServerTime);

// A separate DI type avoids replacing the shared host's TimeProvider registration.
public class CentSmsClock : TimeProvider;

public sealed partial class CentSmsAuth(CentDbContext db, IConfiguration config, ICentSmsSender sender, CentSmsClock clock)
{
    private int Setting(string name, int fallback, int min, int max) => Math.Clamp(config.GetValue("Cent:Sms:" + name, fallback), min, max);
    private byte[] Key()
    {
        if (!config.GetValue<bool>("Cent:Sms:Enabled")) throw CentMelipayamak.Unavailable();
        try {
            var key = Convert.FromBase64String(config["Cent:Sms:VerifierKey"] ?? "");
            if (key.Length < 32) throw new FormatException();
            return key;
        } catch (FormatException) { throw CentMelipayamak.Unavailable(); }
    }
    private static string Mac(byte[] key, string value) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)));
    private static string Proof(byte[] key, string hash, string phone, string code, Guid? target) => Mac(key, $"otp:{hash}:{phone}:{target}:{code}");

    public async Task<CentSmsRequested> Request(string input, string ip, Guid? target, CancellationToken ct, bool recovery = false)
    {
        var phone = CentIranPhone.Normalize(input); var key = Key(); var now = clock.GetUtcNow();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var hash = ChallengeHash(id, recovery);
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var cooldown = Setting("ResendSeconds", 60, 60, 600);
        var challenge = new CentSmsChallenge { IdHash = hash, Phone = phone,
            ExpiresAt = now.AddSeconds(Setting("LifetimeSeconds", 120, 60, 120)), ResendAt = now.AddSeconds(cooldown) };
        // Serialize durable budget reservations and supersession across instances. On contention fail closed.
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)) {
            if (!recovery && target is { } userId &&
                await db.Set<CentMobileIdentity>().AnyAsync(i => i.UserId == userId, ct))
                throw new CentException(409, "phone_already_linked", "حساب شما از قبل شماره موبایل تأییدشده دارد.");
            await Budget("global", Setting("GlobalPerMinute", 100, 1, 1000), 60, 0, now, ct);
            await Budget("ip:" + Mac(key, ip), Setting("IpPerHour", 20, 1, 100), 3600, 0, now, ct);
            await Budget("phone:" + Mac(key, phone), Setting("PhonePerHour", 5, 1, 10), 3600, cooldown, now, ct);
            if (recovery) target = await db.Set<CentMobileIdentity>().Where(i => i.Phone == phone)
                .Select(i => (Guid?)i.UserId).SingleOrDefaultAsync(ct);
            challenge.LinkUserId = target;
            challenge.Verifier = Proof(key, hash, phone, code, target);
            await db.Set<CentSmsChallenge>().Where(c => c.Phone == phone && !c.Consumed).ExecuteUpdateAsync(u => u.SetProperty(c => c.Consumed, true), ct);
            // Retain only a short bounded diagnostic window; never retain OTP verifiers indefinitely.
            await db.Set<CentSmsChallenge>().Where(c => c.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
            await db.Set<CentSmsBudget>().Where(b => b.ResetAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
            db.Add(challenge); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        // Delivery behaves the same for unknown numbers. Recovery still requires a pre-existing verified identity.
        try {
            await sender.Send(phone, code, recovery ? CentSmsPurpose.PasswordReset : CentSmsPurpose.SignIn, ct);
            await db.Set<CentSmsChallenge>().Where(c => c.IdHash == hash && !c.Consumed).ExecuteUpdateAsync(u => u.SetProperty(c => c.Delivered, true), ct);
        } catch {
            // A cancelled HTTP request must not leave a usable, uncertain-delivery challenge.
            await Invalidate();
            throw;
        }
        return new(id, phone, challenge.ExpiresAt, challenge.ResendAt, clock.GetUtcNow());

        Task<int> Invalidate() => db.Set<CentSmsChallenge>().Where(c => c.IdHash == hash)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Consumed, true), CancellationToken.None);
    }

    private static string ChallengeHash(string id, bool recovery) => CentModule.Hash(recovery ? "password-reset:" + id : id);

    private async Task Budget(string name, int limit, int seconds, int delay, DateTimeOffset now, CancellationToken ct)
    {
        var budget = await db.Set<CentSmsBudget>().SingleOrDefaultAsync(b => b.Key == name, ct);
        if (budget is null) { budget = new() { Key = name }; db.Add(budget); }
        if (budget.ResetAt <= now) { budget.ResetAt = now.AddSeconds(seconds); budget.Count = 0; }
        if (budget.NextAt > now || budget.Count >= limit) {
            var retryAt = budget.Count >= limit ? budget.ResetAt : budget.NextAt;
            throw new CentException(429, "sms_rate_limited", "درخواست‌ها زیاد است. کمی بعد دوباره تلاش کنید.", Math.Max(1, (int)Math.Ceiling((retryAt - now).TotalSeconds)));
        }
        budget.Count++; budget.NextAt = now.AddSeconds(delay);
    }

    public async Task<object?> Verify(CentSmsVerify input, Guid? target, CancellationToken ct, bool recovery = false)
    {
        var key = Key(); var now = clock.GetUtcNow();
        if (input.ChallengeId is null || !Regex.IsMatch(input.ChallengeId, "\\A[0-9A-F]{64}\\z") ||
            input.Code is null || !Regex.IsMatch(input.Code, "\\A[0-9]{6}\\z")) throw Invalid();
        if (recovery || input.Password is not null) ValidateNewPassword(input.Password);
        var hash = ChallengeHash(input.ChallengeId, recovery);
        var max = Setting("MaxAttempts", 5, 1, 5);
        var row = await db.Set<CentSmsChallenge>().AsNoTracking().SingleOrDefaultAsync(c => c.IdHash == hash, ct);
        if (row is null || (!recovery && row.LinkUserId != target) ||
            row.Consumed || !row.Delivered || row.ExpiresAt <= now || row.Attempts >= max) throw Invalid();
        // Reserve an attempt atomically BEFORE comparing. Parallel guesses each spend a slot.
        var budget = db.Set<CentSmsChallenge>().Where(c => c.IdHash == hash && !c.Consumed && c.Delivered && c.Attempts < max && c.ExpiresAt > now);
        if (await budget.ExecuteUpdateAsync(u => u.SetProperty(c => c.Attempts, c => c.Attempts + 1), ct) != 1) throw Invalid();
        var valid = CryptographicOperations.FixedTimeEquals(Convert.FromHexString(row.Verifier), Convert.FromHexString(Proof(key, hash, row.Phone, input.Code, row.LinkUserId)));
        if (!valid) throw Invalid();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var consumeAt = clock.GetUtcNow();
        if (await db.Set<CentSmsChallenge>().Where(c => c.IdHash == hash && !c.Consumed && c.ExpiresAt > consumeAt)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Consumed, true), ct) != 1) throw Invalid();
        var identity = await db.Set<CentMobileIdentity>().SingleOrDefaultAsync(i => i.Phone == row.Phone, ct);
        if (recovery) {
            if (identity is null || identity.UserId != row.LinkUserId) {
                await tx.CommitAsync(ct);
                throw new CentException(409, "no_recoverable_account", "حساب تأییدشده‌ای برای بازیابی وجود ندارد. می‌توانید با کد پیامکی ثبت‌نام کنید.");
            }
            await ResetPassword(identity.UserId, input.Password!, consumeAt, ct);
            await tx.CommitAsync(ct);
            return null;
        }
        CentUser user;
        if (target is { } userId) {
            if (identity is not null && identity.UserId != userId || await db.Set<CentMobileIdentity>().AnyAsync(i => i.UserId == userId && i.Phone != row.Phone, ct)) throw Conflict();
            user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        } else if (identity is not null) {
            if (input.Password is not null) {
                await tx.CommitAsync(ct);
                throw new CentException(409, "already_registered", "این شماره حساب دارد. وارد شوید یا رمز عبور را بازیابی کنید.");
            }
            user = await db.Users.SingleAsync(u => u.Id == identity.UserId, ct);
        } else {
            user = new CentUser { Name = "کاربر ایتاتست" };
            // Keep the established required/unique email columns without pretending phone is an email identity.
            user.Email = $"mobile-{user.Id:N}@accounts.itatest.invalid"; user.NormalizedEmail = user.Email.ToUpperInvariant(); db.Add(user);
            if (input.Password is not null) user.PasswordHash = PasswordHasher.HashPassword(user, input.Password);
        }
        if (identity is null) db.Add(new CentMobileIdentity { Phone = row.Phone, UserId = user.Id, VerifiedAt = now });
        var result = await CentAuthEndpoints.StartSession(user, db, ct);
        await tx.CommitAsync(ct); return result;
    }
    private static CentException Invalid() => new(401, "invalid_sms_code", "کد معتبر نیست یا مهلت آن پایان یافته است. کد را بررسی کنید یا دوباره درخواست دهید.");
    private static CentException Conflict() => new(409, "identity_conflict", "این هویت به حساب دیگری متصل است یا حساب شما شماره دیگری دارد. حساب‌ها خودکار ادغام نمی‌شوند.");
}

public static class CentSmsEndpoints
{
    public static void MapCentSms(this RouteGroupBuilder auth)
    {
        Map(auth.MapGroup("/sms"), false);
        Map(auth.MapGroup("/identities/sms").RequireAuthorization("CentUser"), true);
        Map(auth.MapGroup("/password/reset"), false, recovery: true);
        var password = auth.MapGroup("/password");
        AddJsonFilter(password);
        password.MapPost("/login", async (CentSmsAuth service, HttpContext http, CancellationToken ct) => {
            var input = await CentGoogleGisEndpoints.ReadJson<CentPasswordLogin>(http.Request, 2048, ct);
            return Results.Ok(await service.SignInWithPassword(input, ClientIp(http), ct));
        });
    }

    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static void AddJsonFilter(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) => {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.ContentLength > 2048) return Results.StatusCode(413);
            if (http.Request.ContentType?.Split(';')[0].Trim() != "application/json") return Results.StatusCode(415);
            try { return await next(context); }
            catch (CentException e) when (e.Status == 429) { http.Response.Headers.RetryAfter = (e.RetryAfterSeconds ?? 60).ToString(CultureInfo.InvariantCulture); throw; }
        });
    }

    private static void Map(RouteGroupBuilder group, bool link, bool recovery = false)
    {
        AddJsonFilter(group);
        group.MapPost("/request", async (CentSmsAuth service, HttpContext http, CancellationToken ct) => {
            var input = await CentGoogleGisEndpoints.ReadJson<CentSmsRequest>(http.Request, 2048, ct);
            return Results.Ok(await service.Request(input.Phone, ClientIp(http), link ? http.User.UserId() : null, ct, recovery));
        });
        group.MapPost("/verify", async (CentSmsAuth service, HttpContext http, CancellationToken ct) => {
            var input = await CentGoogleGisEndpoints.ReadJson<CentSmsVerify>(http.Request, 2048, ct);
            if (link && input.Password is not null) throw new CentException(400, "invalid_request", "درخواست معتبر نیست.");
            var result = await service.Verify(input, link ? http.User.UserId() : null, ct, recovery);
            return recovery ? Results.NoContent() : Results.Ok(result);
        });
    }
}
