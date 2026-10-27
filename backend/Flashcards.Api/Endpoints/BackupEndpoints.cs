using System.Security.Claims;
using System.Text.Json;
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
        backups.MapPost("/", async (bool? includeRestoreCapability, HttpRequest httpRequest, ClaimsPrincipal principal, BackupService service, CancellationToken cancellationToken) =>
        {
            var request = await ReadRequestAsync<CreateBackupRequest>(httpRequest, cancellationToken);
            var result = await service.CreateAsync(AuthenticatedIdentity.FromClaims(principal).UserId, request, cancellationToken);
            var metadata = ExposeRestore(result.Backup, includeRestoreCapability);
            return result.Replayed ? Results.Ok(metadata) : Results.Created($"/api/v1/backups/{metadata.Id}", metadata);
        }).Accepts<CreateBackupRequest>("application/json").RequireRateLimiting("backup-create");
        backups.MapGet("/status", async (bool? includeRestoreCapability, ClaimsPrincipal principal, BackupService service, CancellationToken cancellationToken) =>
        {
            var status = await service.StatusAsync(AuthenticatedIdentity.FromClaims(principal).UserId, cancellationToken);
            return status with
            {
                RestoreSupported = includeRestoreCapability == true && status.RestoreSupported,
                LatestCompleted = status.LatestCompleted is null ? null : ExposeRestore(status.LatestCompleted, includeRestoreCapability)
            };
        });
        backups.MapGet("/", async (int? page, int? limit, bool? includeRestoreCapability, ClaimsPrincipal principal, BackupService service, CancellationToken cancellationToken) =>
        {
            var result = await service.ListAsync(AuthenticatedIdentity.FromClaims(principal).UserId, page ?? 1, limit ?? 25, cancellationToken);
            return result with { Items = result.Items.Select(item => ExposeRestore(item, includeRestoreCapability)).ToList() };
        });
        backups.MapGet("/{backupId:guid}", async (Guid backupId, bool? includeRestoreCapability, ClaimsPrincipal principal, BackupService service, CancellationToken cancellationToken) =>
        {
            var backup = await service.GetAsync(AuthenticatedIdentity.FromClaims(principal).UserId, backupId, cancellationToken);
            return backup is null ? Results.NotFound() : Results.Ok(ExposeRestore(backup, includeRestoreCapability));
        });
        backups.MapGet("/{backupId:guid}/download", async (Guid backupId, ClaimsPrincipal principal, BackupService service, CancellationToken cancellationToken) =>
            Results.File(await service.DownloadAsync(AuthenticatedIdentity.FromClaims(principal).UserId, backupId, cancellationToken),
                "application/json", $"lerona-backup-{backupId:D}.json")).RequireRateLimiting("backup-create");
        backups.MapPost("/{backupId:guid}/restore", async (Guid backupId, HttpRequest httpRequest, ClaimsPrincipal principal,
            BackupRestoreService service, CancellationToken cancellationToken) =>
        {
            var request = await ReadRequestAsync<RestoreBackupRequest>(httpRequest, cancellationToken);
            return Results.Ok(await service.RestoreAsync(AuthenticatedIdentity.FromClaims(principal).UserId, backupId, request, cancellationToken));
        }).Accepts<RestoreBackupRequest>("application/json").RequireRateLimiting("backup-create");
    }

    private static BackupMetadata ExposeRestore(BackupMetadata metadata, bool? includeRestoreCapability) =>
        metadata with { CanRestore = includeRestoreCapability == true && metadata.CanRestore };

    private static async Task<TRequest> ReadRequestAsync<TRequest>(HttpRequest request, CancellationToken cancellationToken)
    {
        const int maxBytes = 4096;
        if (request.ContentLength > maxBytes)
            throw new BackupException(413, "backup_request_too_large", "Backup requests must not exceed 4096 bytes.");
        if (!request.HasJsonContentType())
            throw new BackupException(415, "backup_json_required", "A JSON request is required.");
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new BackupException(413, "backup_request_too_large", "Backup requests must not exceed 4096 bytes.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<TRequest>(buffer.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                RespectRequiredConstructorParameters = true
            }) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new BackupException(400, "invalid_backup_request", "Provide a valid, complete backup request.");
        }
    }
}
