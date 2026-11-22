using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cent.Module.Exams;

public static class CentExamEndpoints
{
    public static IServiceCollection AddCentExams(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ICentExamStore, CentExamStore>();
        services.TryAddScoped<ICentMockAdmission, ExistingCentMockAdmission>();
        services.AddScoped<CentExamService>();
        services.AddHostedService<CentExamDeadlineWorker>();
        return services;
    }
    public static void MapCentExams(this RouteGroupBuilder cent)
    {
        var group = cent.MapGroup("/assessment");
        group.AddEndpointFilter(async (context, next) => {
            try { return await next(context); }
            catch (CentException e) { return Results.Json(new { code = e.Code, message = e.Message,
                currentState = e.Data["currentState"] }, statusCode: e.Status); }
        });
        group.MapGet("/exam-versions", async (CentExamService s, CancellationToken ct) => Results.Ok(await s.Versions(ct)));
        group.MapGet("/exam-versions/{versionId}/configuration", async (string versionId, CentExamService s, CancellationToken ct) => Results.Ok(await s.Configuration(versionId, ct)));
        var attempts = group.MapGroup("/mock-attempts").RequireAuthorization("CentUser");
        attempts.MapPost("/", async (StartMockExam input, ClaimsPrincipal p, CentExamService s, CancellationToken ct) => {
            var response = await s.Start(p.UserId(), input, ct);
            return Results.Json(response.State, statusCode: response.Created ? 201 : 200);
        });
        attempts.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal p, CentExamService s, CancellationToken ct) => Results.Ok(await s.Read(p.UserId(), id, false, ct)));
        attempts.MapGet("/{id:guid}/active-section", async (Guid id, ClaimsPrincipal p, CentExamService s, CancellationToken ct) => Results.Ok(await s.Read(p.UserId(), id, true, ct)));
        // JsonElement preserves missing vs explicit null selectedOptionId. Binding a
        // nullable string alone would silently interpret a missing field as a clear.
        attempts.MapPut("/{id:guid}/answers", async (Guid id, JsonElement input, ClaimsPrincipal p, CentExamService s, CancellationToken ct) => {
            ExamCommand command;
            try { command = input.Deserialize<ExamCommand>(CentExamJson.Options) ?? throw new JsonException(); }
            catch (JsonException) { throw new CentException(400, "invalid_answer", "Malformed answer command."); }
            command = command with { SelectionWasProvided = input.TryGetProperty("selectedOptionId", out _) };
            return Results.Ok(await s.Command(p.UserId(), id, "answer", command, ct));
        });
        attempts.MapPost("/{id:guid}/advance", async (Guid id, ExamCommand input, ClaimsPrincipal p, CentExamService s, CancellationToken ct) => Results.Ok(await s.Command(p.UserId(), id, "advance", input, ct)));
        attempts.MapPost("/{id:guid}/complete", async (Guid id, ExamCommand input, ClaimsPrincipal p, CentExamService s, CancellationToken ct) => Results.Ok(await s.Command(p.UserId(), id, "complete", input, ct)));
        attempts.MapGet("/{id:guid}/result", async (Guid id, ClaimsPrincipal p, CentExamService s, CancellationToken ct) => Results.Ok(await s.Result(p.UserId(), id, ct)));
        attempts.MapGet("/{id:guid}/review", async (Guid id, ClaimsPrincipal p, CentExamService s, CancellationToken ct) => Results.Ok(await s.Review(p.UserId(), id, ct)));
        attempts.MapGet("/", async (Guid? cursor, int? limit, ClaimsPrincipal p, CentExamService s, CancellationToken ct) => Results.Ok(await s.History(p.UserId(), cursor, limit ?? 20, ct)));

        var admin = cent.MapGroup("/admin/exam-versions").RequireAuthorization("CentAdmin");
        admin.MapPost("/", async (ExamDefinition input, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            input.Validate();
            var blueprint = input.CycleStartDate == CentExamBlueprints.Historical.CycleStartDate ? CentExamBlueprints.Historical : CentExamBlueprints.Current;
            CentModule.Check(input.CycleStartDate == blueprint.CycleStartDate && input.CycleEndDateInclusive == blueprint.CycleEndDateInclusive &&
                input.Sections.SequenceEqual(blueprint.Sections), "Definition must preserve its official blueprint.");
            CentModule.Check(!await db.ExamVersions.AnyAsync(v => v.Id == input.ExamVersionId || v.ConfigurationRevision == input.ConfigurationRevision, ct),
                "Publish a new identity for each revision.", 409, "configuration_changed");
            if (input.SyllabusRevisionId is { } syllabus)
                CentModule.Check(await db.SyllabusRevisions.AnyAsync(s => s.Id == syllabus, ct), "Unknown syllabus revision.");
            if (input.ContentReleaseId is { } release)
                CentModule.Check(await db.ExamContentReleases.AnyAsync(r => r.Id == release && r.ExamVersionId == input.ExamVersionId && r.SyllabusRevisionId == input.SyllabusRevisionId, ct), "Release does not match this definition.");
            var row = new CentExamVersion { Id = input.ExamVersionId, ConfigurationRevision = input.ConfigurationRevision,
                DefinitionJson = CentExamJson.Write(input), SyllabusRevisionId = input.SyllabusRevisionId,
                ContentReleaseId = input.ContentReleaseId, Status = "PUBLISHED" };
            db.ExamVersions.Add(row); CentAdminEndpoints.Audit(db, p, "PUBLISH_EXAM_VERSION", Guid.Empty);
            await db.SaveChangesAsync(ct); return Results.Created($"/api/cent/v1/assessment/exam-versions/{row.Id}/configuration", new { row.Id, row.ConfigurationRevision });
        });
        admin.MapPost("/{versionId}/publish", async (string versionId, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var row = await db.ExamVersions.SingleOrDefaultAsync(v => v.Id == versionId, ct);
            CentModule.Check(row is not null && row.Status == "DRAFT", "Only an imported draft can be published.", 409, "exam_unavailable");
            var d = CentExamJson.Read<ExamDefinition>(row!.DefinitionJson); d.Validate();
            CentModule.Check(row.Id == d.ExamVersionId && row.ConfigurationRevision == d.ConfigurationRevision &&
                row.ContentReleaseId == d.ContentReleaseId && row.SyllabusRevisionId == d.SyllabusRevisionId,
                "Draft metadata does not match its immutable definition.", 409, "exam_unavailable");
            var release = await db.ExamContentReleases.AsNoTracking().SingleOrDefaultAsync(r => r.Id == d.ContentReleaseId, ct);
            CentExamAssembly.Ready(release is not null && release.ExamVersionId == row.Id && release.SyllabusRevisionId == d.SyllabusRevisionId);
            CentExamAssembly.Assemble(p.UserId(), Guid.NewGuid(), d, CentExamJson.Read<ExamContentManifest>(release!.ManifestJson), "publication-validation", DateTimeOffset.UnixEpoch);
            row.Status = "PUBLISHED"; row.AvailableForNewStarts = false; row.Revision++;
            CentAdminEndpoints.Audit(db, p, "PUBLISH_EXAM_VERSION", Guid.Empty);
            await db.SaveChangesAsync(ct); return Results.Ok(new { row.Id, row.Status, row.AvailableForNewStarts });
        });
        admin.MapPost("/{versionId}/archive", async (string versionId, ClaimsPrincipal p, CentDbContext db, CancellationToken ct) => {
            var row = await db.ExamVersions.SingleOrDefaultAsync(v => v.Id == versionId, ct);
            CentModule.Check(row is not null, "Exam version not found.", 404);
            row!.Status = "ARCHIVED"; row.AvailableForNewStarts = false; row.Revision++;
            CentAdminEndpoints.Audit(db, p, "ARCHIVE_EXAM_VERSION", Guid.Empty);
            await db.SaveChangesAsync(ct); return Results.Ok(new { row.Id, row.Status });
        });
        admin.MapPost("/{versionId}/activate", async (string versionId, ClaimsPrincipal p, CentDbContext db, ICentMockAdmission admission, CancellationToken ct) => {
            var row = await db.ExamVersions.SingleOrDefaultAsync(v => v.Id == versionId, ct);
            CentModule.Check(row is not null && row.Status == "PUBLISHED", "Only a published definition can be activated.", 409, "exam_unavailable");
            var d = CentExamJson.Read<ExamDefinition>(row!.DefinitionJson); d.Validate();
            CentModule.Check(admission.IsConfigured && d.SubmissionPolicyId is not null && d.ContentReleaseId is not null && d.SyllabusRevisionId is not null,
                "Approval and content gates must be resolved before activation.", 409, "exam_unavailable");
            var release = await db.ExamContentReleases.AsNoTracking().SingleOrDefaultAsync(r => r.Id == d.ContentReleaseId, ct);
            CentExamAssembly.Ready(release?.ReviewedAt is not null && release.ReviewedBy is not null && release.ExamVersionId == d.ExamVersionId && release.SyllabusRevisionId == d.SyllabusRevisionId);
            // Full exact-quota/dependency validation, with no persisted or timed attempt.
            CentExamAssembly.Assemble(p.UserId(), Guid.NewGuid(), d, CentExamJson.Read<ExamContentManifest>(release!.ManifestJson), "activation-validation", DateTimeOffset.UnixEpoch);
            row.AvailableForNewStarts = true; row.Revision++;
            CentAdminEndpoints.Audit(db, p, "ACTIVATE_EXAM_VERSION", Guid.Empty);
            await db.SaveChangesAsync(ct); return Results.Ok(new { row.Id, row.AvailableForNewStarts });
        });
    }
}

public sealed class CentExamDeadlineWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CentExamDeadlineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken)) {
            try {
                Guid[] due;
                using (var scope = scopes.CreateScope()) due = await scope.ServiceProvider.GetRequiredService<ICentExamStore>().Due(clock.GetUtcNow(), stoppingToken);
                foreach (var id in due) {
                    try {
                        using var scope = scopes.CreateScope();
                        var store = scope.ServiceProvider.GetRequiredService<ICentExamStore>();
                        var owner = await store.Owner(id, stoppingToken);
                        if (owner is { } user) await scope.ServiceProvider.GetRequiredService<CentExamService>().Read(user, id, false, stoppingToken);
                    } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception e) { logger.LogWarning("Cent exam {AttemptId} reconciliation failed: {Type}", id, e.GetType().Name); }
                }
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning("Cent exam deadline reconciliation failed: {Type}", e.GetType().Name); }
        }
    }
}
