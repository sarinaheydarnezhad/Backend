using System.ComponentModel.DataAnnotations;
using Flashcards.Infrastructure.Auth;

namespace Flashcards.Api.Endpoints;

public static class PhoneAuthEndpoints
{
    public static RouteGroupBuilder MapPhoneAuth(this RouteGroupBuilder auth)
    {
        var phone = auth.MapGroup("/phone").AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (PhoneAuthException exception)
            {
                return Results.Json(new { error = exception.Message, code = exception.Code }, statusCode: exception.StatusCode);
            }
        });
        phone.MapPost("/otp/request", (RequestOtpRequest request, PhoneAuthService service, CancellationToken cancellationToken) =>
            service.RequestAsync(request.PhoneNumber, request.Purpose, cancellationToken));
        phone.MapPost("/otp/verify", (VerifyOtpRequest request, PhoneAuthService service, CancellationToken cancellationToken) =>
            service.VerifyAsync(request.ChallengeId, request.Code, cancellationToken));
        phone.MapPost("/register", (PhoneRegisterRequest request, PhoneAuthService service, CancellationToken cancellationToken) =>
            service.RegisterAsync(request.PhoneNumber, request.RegistrationToken, request.Password,
                request.ConfirmPassword, request.Username, cancellationToken));
        phone.MapPost("/login", (PhoneLoginRequest request, PhoneAuthService service, CancellationToken cancellationToken) =>
            service.LoginAsync(request.PhoneNumber, request.Password, cancellationToken));
        return auth;
    }
}

public sealed record RequestOtpRequest([property: Required, StringLength(32)] string PhoneNumber, [property: Required, StringLength(12)] string Purpose);
public sealed record VerifyOtpRequest(Guid ChallengeId, [property: Required, StringLength(32)] string Code);
public sealed record PhoneRegisterRequest(
    [property: Required, StringLength(32)] string PhoneNumber,
    [property: Required, StringLength(128)] string RegistrationToken,
    [property: Required, StringLength(128, MinimumLength = 12)] string Password,
    [property: Required, StringLength(128)] string ConfirmPassword,
    [property: Required, StringLength(120)] string Username);
public sealed record PhoneLoginRequest([property: Required, StringLength(32)] string PhoneNumber,
    [property: Required, StringLength(128)] string Password);
