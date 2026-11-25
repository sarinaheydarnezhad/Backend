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
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.AddScoped<CentAccess>();
        services.AddScoped<CentPayments>();
        services.AddScoped<CentSubjectCommerce>();
        services.AddScoped<CentPractice>();
        services.AddScoped<CentLearning>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<CentReaderProtection>();
        services.AddScoped<CentReaderCleanup>();
        services.AddHostedService<CentReaderCleanupWorker>();
        services.AddSingleton(sp => new CentPilotProtection(config, sp.GetRequiredService<ILogger<CentPilotProtection>>(), TimeProvider.System));
        if (config.GetValue<bool>("Cent:Exams:Enabled")) Exams.CentExamEndpoints.AddCentExams(services);
        services.AddCentSms(config);
        services.AddScoped<CentGoogleAuth>();
        services.AddScoped<CentGoogleGisAuth>();
        services.AddSingleton<CentGooglePublicKeys>();
        services.AddSingleton<ICentGoogleIdTokenValidator, CentGoogleIdTokenValidator>();
        services.AddHttpClient(CentGooglePublicKeys.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<CentGateway>(c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton(new CentAdmission());
        var origins = config.GetSection("Cent:AllowedOrigins").Get<string[]>() ?? [];
        foreach (var origin in origins) CentGoogleSettings.ValidateOrigin(origin);
        services.AddCors(options => options.AddPolicy("CentOrigins", policy => policy
            .WithOrigins(origins)
            .AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Retry-After")));
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, CentSessionHandler>(Scheme, _ => { });
        services.AddAuthorization(options => {
            options.AddPolicy("CentUser", p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());
            CentAdminSecurity.Policies(options);
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
            catch (CentException e) {
                if (e.RetryAfterSeconds is { } retry) response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (context.HttpContext.Request.Path.StartsWithSegments("/api/cent/v1/admin") &&
                    context.HttpContext.Request.Method is "POST" or "PUT" or "DELETE" &&
                    CentAdminSecurity.IsAdmin(context.HttpContext.User.FindFirstValue(ClaimTypes.Role))) {
                    var db = context.HttpContext.RequestServices.GetRequiredService<CentDbContext>();
                    db.ChangeTracker.Clear();
                    var target = Guid.TryParse(context.HttpContext.Request.RouteValues["id"]?.ToString(), out var id) ? id : Guid.Empty;
                    CentAdminEndpoints.Audit(db, context.HttpContext.User, $"ADMIN_REJECTED_{e.Status}", target);
                    await db.SaveChangesAsync(context.HttpContext.RequestAborted);
                }
                return Results.Json(new { message = e.Message, code = e.Code }, statusCode: e.Status);
            }
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
        group.MapCentSubjects();
        group.MapCentAdmin();
        group.MapCentPractice();
        group.MapCentLearning();
        if (app.Configuration.GetValue<bool>("Cent:Exams:Enabled")) Exams.CentExamEndpoints.MapCentExams(group);
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

public sealed class CentException(int status, string code, string message, int? retryAfterSeconds = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
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
            new Claim(ClaimTypes.Email, CentAuthEndpoints.PublicEmail(row.Email)), new Claim(ClaimTypes.Role, row.Role),
            new Claim("cent_session", row.SessionId.ToString())
        ], CentModule.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), CentModule.Scheme));
    }
}

public static class CentAuthEndpoints
{
    public static void MapCentAuth(this RouteGroupBuilder group)
    {
        var auth = group.MapGroup("/auth").RequireRateLimiting("cent-auth");
        auth.MapCentGoogleGis();
        auth.MapCentSms();
        // Explicit operator secret bound to an existing verified, authenticated identity.
        auth.MapPost("/bootstrap", async (ClaimsPrincipal principal, HttpContext http, IConfiguration config, CentDbContext db, CancellationToken ct) => {
            var expected = config["Cent:BootstrapToken"] ?? "";
            var supplied = http.Request.Headers["X-Cent-Bootstrap"].ToString();
            CentModule.Check(expected.Length >= 32 && supplied.Length <= 256 && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied))), "این مسیر فعال نیست.", 404);
            CentModule.Check(Guid.TryParse(config["Cent:BootstrapUserId"], out var target) && target == principal.UserId(), "این مسیر فعال نیست.", 404);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var user = await db.Users.SingleAsync(u => u.Id == target, ct);
            CentModule.Check(!string.IsNullOrEmpty(user.GoogleSubject), "هویت تأییدشده لازم است.", 409);
            CentModule.Check(!await db.Users.AnyAsync(u => u.Role == "ADMIN" || u.Role == "SuperAdmin", ct), "مدیر ارشد قبلاً تعیین شده است.", 409);
            user.Role = "SuperAdmin";
            CentAdminEndpoints.Audit(db, principal, "BOOTSTRAP_SUPERADMIN_OK", user.Id);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Ok(new { user.Id, user.Role });
        }).RequireAuthorization("CentUser");
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

    internal static async Task<object> StartSession(CentUser user, CentDbContext db, CancellationToken ct)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new CentSession { UserId = user.Id, TokenHash = CentModule.Hash(token), ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) };
        db.Sessions.Add(session); await db.SaveChangesAsync(ct);
        return new { token, expiresAt = session.ExpiresAt, user = new { user.Id, user.Name, email = PublicEmail(user.Email), user.Role } };
    }
    internal static string PublicEmail(string email) => email.EndsWith("@accounts.itatest.invalid", StringComparison.OrdinalIgnoreCase) ? "" : email;
}
