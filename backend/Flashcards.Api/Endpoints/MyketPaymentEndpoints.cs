using System.Security.Claims;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Subscriptions;

namespace Flashcards.Api.Endpoints;

public static class MyketPaymentEndpoints
{
    public sealed record CheckoutRequest(int DurationMonths);

    public static void MapMyketPayments(this RouteGroupBuilder v1)
    {
        var payments = v1.MapGroup("/payments/myket").WithTags("payments").RequireAuthorization()
            .RequireRateLimiting("auth")
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                try { return await next(context); }
                catch (PaymentException exception)
                {
                    return Results.Json(new { error = exception.Message, code = exception.Code }, statusCode: exception.StatusCode);
                }
            });
        payments.MapGet("/", (MyketPaymentService service) => service.Configuration());
        payments.MapPost("/orders", (CheckoutRequest request, ClaimsPrincipal principal, MyketPaymentService service,
            CancellationToken cancellationToken) => service.BeginAsync(AuthenticatedIdentity.FromClaims(principal).UserId,
                request.DurationMonths, cancellationToken));
        payments.MapPost("/verify", (VerifyMyketRequest request, ClaimsPrincipal principal, MyketPaymentService service,
            CancellationToken cancellationToken) => service.VerifyAsync(AuthenticatedIdentity.FromClaims(principal).UserId,
                request, cancellationToken));
        payments.MapPost("/orders/{id:guid}/cancel", async (Guid id, ClaimsPrincipal principal, MyketPaymentService service,
            CancellationToken cancellationToken) =>
        {
            await service.CancelAsync(AuthenticatedIdentity.FromClaims(principal).UserId, id, cancellationToken);
            return Results.NoContent();
        });
    }
}
