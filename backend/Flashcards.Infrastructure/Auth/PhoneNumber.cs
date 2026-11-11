using System.Text.RegularExpressions;

namespace Flashcards.Infrastructure.Auth;

public static class PhoneNumber
{
    public static string Normalize(string? value)
    {
        if (value is null || value.Length > 32)
            throw new PhoneAuthException(400, "invalid_phone", "Enter a mobile number with its international country code.");
        var normalized = string.Concat(value.Trim().Select(character => character switch
        {
            >= '\u06f0' and <= '\u06f9' => (char)('0' + character - '\u06f0'),
            >= '\u0660' and <= '\u0669' => (char)('0' + character - '\u0660'),
            _ => character
        }));
        if (Regex.IsMatch(normalized, "^09[0-9]{9}$", RegexOptions.CultureInvariant))
            normalized = "+98" + normalized[1..];
        if (!Regex.IsMatch(normalized, "^\\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant))
            throw new PhoneAuthException(400, "invalid_phone", "Enter a mobile number with its international country code.");
        return normalized;
    }

    public static string NormalizeIranian(string? value)
    {
        var normalized = Normalize(value);
        if (!Regex.IsMatch(normalized, "^\\+989[0-9]{9}$", RegexOptions.CultureInvariant))
            throw new PhoneAuthException(400, "invalid_phone", "Enter a valid Iranian mobile number.");
        return normalized;
    }
}

public sealed class PhoneAuthException(int statusCode, string code, string message, int? retryAfterSeconds = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed record OtpRequestResponse(Guid ChallengeId, DateTimeOffset ExpiresAtUtc, DateTimeOffset ResendAtUtc, int CodeLength = 6);
public sealed record OtpVerificationResponse(string? RegistrationToken, DateTimeOffset? ExpiresAtUtc, AuthTokens? Tokens, string? ResetPasswordToken = null);
