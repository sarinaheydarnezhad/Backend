using System.Security.Claims;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Backups;

namespace Flashcards.Api.Endpoints;

public static class BackupEndpoints
{
    public static void MapBackups(this RouteGroupBuilder v1)
    {
        var backups = v1.MapGroup("/backups").WithTags("backups").RequireAuthorization()
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                try { return await next(context); }
                catch (BackupException exception)
                {
                    return Results.Json(new { error = exception.Message, code = exception.Code, backupId = exception.BackupId },
                        statusCode: exception.StatusCode);
                }
            });
        backups.MapPost("/", async (CreateBackupRequest request, ClaimsPrincipal principal, BackupService service, CancellationToken cancellationToken) =>
        {
            var result = await service.CreateAsync(AuthenticatedIdentity.FromClaims(principal).UserId, request, cancellationToken);
            return result.Replayed ? Results.Ok(result.Backup) : Results.Created($"/api/v1/backups/{result.Backup.Id}", result.Backup);
        }).RequireRateLimiting("backup-create");
        backups.MapGet("/status", (ClaimsPrincipal principal, BackupService service, CancellationToken cancellationToken) =>
            service.StatusAsync(AuthenticatedIdentity.FromClaims(principal).UserId, cancellationToken));
        backups.MapGet("/", (int? page, int? limit, ClaimsPrincipal principal, BackupService service, CancellationToken cancellationToken) =>
            service.ListAsync(AuthenticatedIdentity.FromClaims(principal).UserId, page ?? 1, limit ?? 25, cancellationToken));
        backups.MapGet("/{backupId:guid}", async (Guid backupId, ClaimsPrincipal principal, BackupService service, CancellationToken cancellationToken) =>
        {
            var backup = await service.GetAsync(AuthenticatedIdentity.FromClaims(principal).UserId, backupId, cancellationToken);
            return backup is null ? Results.NotFound() : Results.Ok(backup);
        });
    }
}
