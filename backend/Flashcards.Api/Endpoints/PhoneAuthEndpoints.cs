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
        foreach (var path in new[] { "/activation/request", "/register", "/register-password", "/login" })
            phone.MapPost(path, Disabled);
        return auth;
    }

    public static IResult Disabled(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { code = "sms_otp_only", error = "ورود و ثبت‌نام فقط با کد پیامکی امکان‌پذیر است." }, statusCode: 410);
    }
}

public sealed record RequestOtpRequest([property: Required, StringLength(32)] string PhoneNumber, [property: StringLength(12)] string Purpose = "authenticate");
public sealed record VerifyOtpRequest(Guid ChallengeId, [property: Required, StringLength(6, MinimumLength = 6)] string Code);
