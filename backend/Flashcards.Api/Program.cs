using Flashcards.Api.Endpoints;
using Flashcards.Application;
using Flashcards.Infrastructure;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
var concurrentRequests = builder.Configuration.GetValue("RateLimits:ConcurrentRequests", 64);
if (concurrentRequests is < 1 or > 1024)
    throw new InvalidOperationException("RateLimits:ConcurrentRequests must be between 1 and 1024.");
builder.Services.AddSingleton(_ => new ConcurrencyLimiter(new ConcurrencyLimiterOptions
{
    PermitLimit = concurrentRequests,
    QueueLimit = 0
}));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) : 5;
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return ValueTask.CompletedTask;
    };
    options.AddPolicy("backup-create", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

const string corsPolicy = "ConfiguredOrigins";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy(corsPolicy, policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Retry-After")));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Flashcards API v1"));
}

app.UseCors(corsPolicy);
app.UseRouting();
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health")
    {
        await next(context);
        return;
    }
    var limiter = context.RequestServices.GetRequiredService<ConcurrencyLimiter>();
    using var lease = await limiter.AcquireAsync(1, context.RequestAborted);
    if (!lease.IsAcquired)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = "5";
        context.Response.Headers.CacheControl = "no-store";
        return;
    }
    await next(context);
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapV1Endpoints();

app.Run();

public partial class Program;
