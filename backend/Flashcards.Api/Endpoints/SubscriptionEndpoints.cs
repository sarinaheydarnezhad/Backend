using System.Security.Claims;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Subscriptions;

namespace Flashcards.Api.Endpoints;

public static class SubscriptionEndpoints
{
    public static void MapSubscriptions(this RouteGroupBuilder v1)
    {
        v1.MapGet("/plans", (PlanCatalog catalog, HttpContext context) =>
        {
            context.Response.Headers.Vary = "X-Lerona-Payments";
            return Results.Ok(catalog.Get(context.Request.Headers["X-Lerona-Payments"] == "myket-v1"));
        }).WithTags("subscriptions");
        var mine = v1.MapGroup("/me").WithTags("subscriptions").RequireAuthorization()
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context);
            });
        mine.MapGet("/subscription", (ClaimsPrincipal principal, EntitlementService service, CancellationToken cancellationToken) =>
            service.GetAsync(AuthenticatedIdentity.FromClaims(principal).UserId, cancellationToken));
        mine.MapGet("/entitlements", async (ClaimsPrincipal principal, EntitlementService service, CancellationToken cancellationToken) =>
            (await service.GetAsync(AuthenticatedIdentity.FromClaims(principal).UserId, cancellationToken)).Entitlements);
        mine.MapPost("/study/authorize", async (ClaimsPrincipal principal, EntitlementService service, CancellationToken cancellationToken) =>
        {
            var userId = AuthenticatedIdentity.FromClaims(principal).UserId;
            return Results.Ok(await service.AuthorizeStudyAsync(userId, cancellationToken));
        });
    }
}
