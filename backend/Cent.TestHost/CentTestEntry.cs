using Cent.Module;

namespace Cent.TestHost;

// In-process integration host only. The production composition remains Flashcards.Api.
public sealed class CentTestEntry
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options => {
            options.RejectionStatusCode = 429;
            options.OnRejected = (context, _) => {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
        });
        builder.Services.AddCentModule(builder.Configuration);
        builder.Services.AddHealthChecks();
        var app = builder.Build();
        app.UseRouting();
        app.UseCentModule();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapHealthChecks("/health");
        app.MapCentModule();
        app.Run();
    }
}
