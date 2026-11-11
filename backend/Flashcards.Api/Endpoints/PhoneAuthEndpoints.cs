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
                if (exception.RetryAfterSeconds is { } seconds)
                    context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new { error = exception.Message, code = exception.Code }, statusCode: exception.StatusCode);
            }
        });
        phone.MapPost("/otp/request", (RequestOtpRequest request, PhoneAuthService service, CancellationToken cancellationToken) =>
            service.RequestAsync(request.PhoneNumber, request.Purpose, cancellationToken));
        phone.MapPost("/otp/verify", (VerifyOtpRequest request, PhoneAuthService service, CancellationToken cancellationToken) =>
            service.VerifyAsync(request.ChallengeId, request.Code, cancellationToken));
        phone.MapPost("/register", (RegisterPhoneRequest request, PhoneAuthService service, CancellationToken ct) =>
            service.RegisterAsync(request.PhoneNumber, request.RegistrationToken, request.Password, request.ConfirmPassword, request.Username, ct));
        phone.MapPost("/login", (PhoneLoginRequest request, PhoneAuthService service, CancellationToken ct) =>
            service.LoginAsync(request.PhoneNumber, request.Password, ct));
        phone.MapPost("/password/reset", (ResetPhonePasswordRequest request, PhoneAuthService service, CancellationToken ct) =>
            service.ResetPasswordAsync(request.PhoneNumber, request.ResetToken, request.Password, request.ConfirmPassword, ct));
        phone.MapPost("/register-password", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { code = "phone_verification_required", error = "Verify SMS before creating an account." }, statusCode: 403);
        });
        phone.MapPost("/activation/request", Disabled);
        return auth;
    }

    public static IResult Disabled(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { code = "phone_password_auth_required", error = "Use mobile number and password. SMS verification is required for signup and password reset." }, statusCode: 410);
    }
}

public sealed record RequestOtpRequest([property: Required, StringLength(32)] string PhoneNumber, [property: StringLength(12)] string Purpose = "register");
public sealed record VerifyOtpRequest(Guid ChallengeId, [property: Required, StringLength(6, MinimumLength = 6)] string Code);
public sealed record RegisterPhoneRequest([property: Required, StringLength(32)] string PhoneNumber,
    [property: Required, StringLength(128)] string RegistrationToken,
    [property: Required, StringLength(128)] string Password,
    [property: Required, StringLength(128)] string ConfirmPassword,
    [property: Required, StringLength(120)] string Username);
public sealed record PhoneLoginRequest([property: Required, StringLength(32)] string PhoneNumber,
    [property: Required, StringLength(128)] string Password);
public sealed record ResetPhonePasswordRequest([property: Required, StringLength(32)] string PhoneNumber,
    [property: Required, StringLength(128)] string ResetToken,
    [property: Required, StringLength(128)] string Password,
    [property: Required, StringLength(128)] string ConfirmPassword);
