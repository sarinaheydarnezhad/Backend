using Flashcards.Domain;

namespace Flashcards.Infrastructure.Auth;

public static class PhoneVerification
{
    public static PhoneAuthException Disabled() => new(410, "sms_otp_only", "ورود فقط با کد پیامکی امکان‌پذیر است.");
    public static bool IsVerified(User user) => user.PhoneNumber is { Length: 13 } phone &&
        phone.StartsWith("+989", StringComparison.Ordinal) && phone[4..].All(char.IsAsciiDigit) &&
        user.VerifiedPhoneNumber == user.PhoneNumber && user.PhoneVerifiedAtUtc is not null;

    public static PhoneAuthException Required() => new(403, "phone_verification_required",
        "Verify your mobile number with SMS before accessing this account.");
}
