using System.ComponentModel.DataAnnotations;
using Flashcards.Infrastructure.Auth;

namespace Flashcards.Api.Endpoints;

public static class GoogleAuthEndpoints
{
    public static RouteGroupBuilder MapGoogleAuth(this RouteGroupBuilder auth)
    {
        auth.MapPost("/google", async (GoogleSignInRequest request, GoogleAuthService service,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await service.SignInAsync(request.IdToken, cancellationToken)); }
            catch (GoogleAuthException exception)
            {
                return Results.Json(new { error = exception.Message, code = exception.Code }, statusCode: exception.StatusCode);
            }
        });
        return auth;
    }
}

public sealed record GoogleSignInRequest([property: Required, StringLength(8192)] string IdToken);
