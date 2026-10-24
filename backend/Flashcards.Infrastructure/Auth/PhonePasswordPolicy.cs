using System.Text.RegularExpressions;

namespace Flashcards.Infrastructure.Auth;

public static class PhonePasswordPolicy
{
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.Ordinal)
    {
        "password1", "password12", "password123", "password1234", "password12345", "password123456",
        "qwerty123", "qwerty12345", "qwerty123456", "abc12345", "abcd1234", "1234abcd", "letmein123",
        "welcome1", "welcome123", "admin123", "admin1234", "iloveyou1", "passw0rd"
    };

    public static bool IsValid(string? password) => password is { Length: >= 8 and <= 128 } &&
        Regex.IsMatch(password, @"\p{L}", RegexOptions.CultureInvariant) &&
        Regex.IsMatch(password, @"\p{Nd}", RegexOptions.CultureInvariant) &&
        !CommonPasswords.Contains(Regex.Replace(password, @"[^\p{L}\p{Nd}]", "", RegexOptions.CultureInvariant).ToLowerInvariant());
}
