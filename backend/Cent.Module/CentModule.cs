using System.Data.Common;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cent.Module;

public static class CentModule
{
    public const string Scheme = "CentSession";
    public static IServiceCollection AddCentModule(this IServiceCollection services, IConfiguration config)
    {
        // Opt in explicitly. No Cent settings, jobs, migrations or database access run on Lerona startup.
        if (!config.GetValue<bool>("Cent:Enabled")) return services;
        services.AddDbContext<CentDbContext>(o => o.UseSqlServer(
            config.GetConnectionString("CentDb") ?? config.GetConnectionString("FlashcardsDb"),
            sql => sql.MigrationsHistoryTable("cent_migrations_history")));
        services.AddScoped<IPasswordHasher<CentUser>, PasswordHasher<CentUser>>();
        services.AddScoped<CentAccess>();
        services.AddScoped<CentPayments>();
        services.AddScoped<CentPractice>();
        services.AddScoped<CentLearning>();
        services.AddHttpClient<CentGateway>(c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton(new CentAdmission());
        services.AddCors(options => options.AddPolicy("CentOrigins", policy => policy
            .WithOrigins(config.GetSection("Cent:AllowedOrigins").Get<string[]>() ?? [])
            .AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Retry-After")));
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, CentSessionHandler>(Scheme, _ => { });
        services.AddAuthorization(options => {
            options.AddPolicy("CentUser", p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());
            options.AddPolicy("CentAdmin", p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireRole("ADMIN"));
        });
        services.AddRateLimiter(options => {
            options.AddPolicy("cent-auth", c => RateLimitPartition.GetFixedWindowLimiter(
                c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("cent-pages", c => RateLimitPartition.GetFixedWindowLimiter(
                c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }

    public static void MapCentModule(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Cent:Enabled")) return;
        var group = app.MapGroup("/api/cent/v1").WithTags("Cent").RequireCors("CentOrigins");
        group.AddEndpointFilter(async (context, next) => {
            var response = context.HttpContext.Response;
            response.Headers.CacheControl = "private, no-store, max-age=0";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            try { return await next(context); }
            catch (CentException e) { return Results.Json(new { message = e.Message, code = e.Code }, statusCode: e.Status); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "این یادداشت در پنجره دیگری تغییر کرده؛ دوباره بارگذاری کنید.", code = "revision_conflict" }); }
            catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException or HttpRequestException or TaskCanceledException) {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Cent")
                    .LogWarning("Cent request failed: {Type}; trace {TraceId}", e.GetType().Name, context.HttpContext.TraceIdentifier);
                return Results.Json(new { message = "سرویس موقتاً در دسترس نیست؛ اطلاعات شما حذف نشده است. دوباره تلاش کنید.", code = "temporarily_unavailable" }, statusCode: 503);
            }
        });
        group.MapGet("/status", (CentGateway gateway) => Results.Ok(new { service = "Cent", payments = gateway.Mode, screenshotBlocking = false }));
        group.MapCentAuth();
        group.MapCentContent();
        group.MapCentCommerce();
        group.MapCentAdmin();
        group.MapCentPractice();
        group.MapCentLearning();
    }

    public static void UseCentModule(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Cent:Enabled")) return;
        // Run after routing, before authentication. Cent gets its own origins and admission budget.
        app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/cent/v1"), branch => {
            branch.UseCors("CentOrigins");
            branch.Use(async (context, next) => {
                context.Response.Headers.CacheControl = "private, no-store, max-age=0";
                using var permit = await context.RequestServices.GetRequiredService<CentAdmission>().Limiter.AcquireAsync(1, context.RequestAborted);
                if (!permit.IsAcquired) { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "5"; return; }
                await next(context);
            });
        });
    }

    public static Guid UserId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    public static void Check(bool condition, string message, int status = 400, string code = "invalid_request")
    {
        if (!condition) throw new CentException(status, code, message);
    }
}

public sealed class CentAdmission : IDisposable
{
    public ConcurrencyLimiter Limiter { get; } = new(new ConcurrencyLimiterOptions { PermitLimit = 4, QueueLimit = 0 });
    public void Dispose() => Limiter.Dispose();
}

public sealed class CentException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class CentSessionHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, CentDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        var raw = header[7..];
        if (raw.Length != 64) return AuthenticateResult.Fail("Invalid Cent session.");
        var hash = CentModule.Hash(raw);
        var now = DateTimeOffset.UtcNow;
        var row = await (from session in db.Sessions.AsNoTracking()
                         join user in db.Users.AsNoTracking() on session.UserId equals user.Id
                         where session.TokenHash == hash && session.RevokedAt == null && session.ExpiresAt > now
                         select new { SessionId = session.Id, UserId = user.Id, user.Name, user.Email, user.Role }).SingleOrDefaultAsync(Context.RequestAborted);
        if (row is null) return AuthenticateResult.Fail("Expired Cent session.");
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, row.UserId.ToString()), new Claim(ClaimTypes.Name, row.Name),
            new Claim(ClaimTypes.Email, row.Email), new Claim(ClaimTypes.Role, row.Role),
            new Claim("cent_session", row.SessionId.ToString())
        ], CentModule.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), CentModule.Scheme));
    }
}

public static class CentAuthEndpoints
{
    public sealed record Credentials(string Email, string Password, string? Name);
    public static void MapCentAuth(this RouteGroupBuilder group)
    {
        var auth = group.MapGroup("/auth").RequireRateLimiting("cent-auth");
        auth.MapPost("/register", async (Credentials input, CentDbContext db, IPasswordHasher<CentUser> hasher, CancellationToken ct) => {
            Validate(input, true);
            var email = input.Email.Trim();
            CentModule.Check(!await db.Users.AnyAsync(u => u.NormalizedEmail == email.ToUpperInvariant(), ct), "این ایمیل قبلاً ثبت شده است.", 409);
            var user = new CentUser { Email = email, NormalizedEmail = email.ToUpperInvariant(), Name = input.Name!.Trim() };
            user.PasswordHash = hasher.HashPassword(user, input.Password);
            db.Users.Add(user);
            return Results.Ok(await StartSession(user, db, ct));
        });
        auth.MapPost("/login", async (Credentials input, CentDbContext db, IPasswordHasher<CentUser> hasher, CancellationToken ct) => {
            Validate(input, false);
            var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == input.Email.Trim().ToUpperInvariant(), ct);
            // Run a hash check even for unknown accounts to reduce account discovery through response timing.
            var candidate = user ?? new CentUser();
            var storedHash = user?.PasswordHash ?? hasher.HashPassword(candidate, "Cent-unknown-account-dummy");
            var verified = hasher.VerifyHashedPassword(candidate, storedHash, input.Password);
            CentModule.Check(user is not null && verified != PasswordVerificationResult.Failed, "ایمیل یا رمز عبور صحیح نیست.", 401);
            if (verified == PasswordVerificationResult.SuccessRehashNeeded) user!.PasswordHash = hasher.HashPassword(user, input.Password);
            return Results.Ok(await StartSession(user!, db, ct));
        });
        // One-time bootstrap: server secret, explicit operator call, never public self-assigned administrator roles.
        auth.MapPost("/bootstrap", async (Credentials input, HttpContext http, IConfiguration config, CentDbContext db,
            IPasswordHasher<CentUser> hasher, CancellationToken ct) => {
            var expected = config["Cent:BootstrapToken"] ?? "";
            var supplied = http.Request.Headers["X-Cent-Bootstrap"].ToString();
            CentModule.Check(expected.Length >= 32 && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied))), "این مسیر فعال نیست.", 404);
            Validate(input, true);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            CentModule.Check(!await db.Users.AnyAsync(u => u.Role == "ADMIN", ct), "مدیر قبلاً ساخته شده است.", 409);
            var user = new CentUser { Name = input.Name!.Trim(), Email = input.Email.Trim(), NormalizedEmail = input.Email.Trim().ToUpperInvariant(), Role = "ADMIN" };
            user.PasswordHash = hasher.HashPassword(user, input.Password); db.Users.Add(user);
            var result = await StartSession(user, db, ct); await transaction.CommitAsync(ct);
            return Results.Ok(result);
        });
        group.MapGet("/me", (ClaimsPrincipal principal) => Results.Ok(new {
            id = principal.UserId(), name = principal.Identity!.Name,
            email = principal.FindFirstValue(ClaimTypes.Email), role = principal.FindFirstValue(ClaimTypes.Role)
        })).RequireAuthorization("CentUser");
        group.MapPost("/auth/logout", async (ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var sessionId = Guid.Parse(p.FindFirstValue("cent_session")!);
            await db.Sessions.Where(s => s.Id == sessionId && s.UserId == p.UserId()).ExecuteUpdateAsync(
                u => u.SetProperty(s => s.RevokedAt, DateTimeOffset.UtcNow), ct);
            return Results.NoContent();
        }).RequireAuthorization("CentUser");
    }

    private static void Validate(Credentials c, bool registration)
    {
        CentModule.Check(!string.IsNullOrWhiteSpace(c.Email) && c.Email.Length <= 256 &&
            System.Net.Mail.MailAddress.TryCreate(c.Email.Trim(), out var address) && address.Address == c.Email.Trim(), "ایمیل معتبر وارد کنید.");
        CentModule.Check(c.Password is { Length: >= 10 and <= 128 }, "رمز عبور باید بین ۱۰ تا ۱۲۸ نویسه باشد.");
        if (registration) CentModule.Check(!string.IsNullOrWhiteSpace(c.Name) && c.Name.Trim().Length <= 120, "نام را وارد کنید (حداکثر ۱۲۰ نویسه).");
    }

    private static async Task<object> StartSession(CentUser user, CentDbContext db, CancellationToken ct)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new CentSession { UserId = user.Id, TokenHash = CentModule.Hash(token), ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) };
        db.Sessions.Add(session); await db.SaveChangesAsync(ct);
        return new { token, expiresAt = session.ExpiresAt, user = new { user.Id, user.Name, user.Email, user.Role } };
    }
}
