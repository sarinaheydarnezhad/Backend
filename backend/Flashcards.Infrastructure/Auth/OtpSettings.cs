using Microsoft.Extensions.Configuration;

namespace Flashcards.Infrastructure.Auth;

public sealed class OtpSettings
{
    public int Length { get; init; } = 6;
    public int ExpirationSeconds { get; init; } = 120;
    public int ResendCooldownSeconds { get; init; } = 60;
    public int MaxAttempts { get; init; } = 5;
    public int MaxRequestsPerHour { get; init; } = 5;

    public static OtpSettings FromConfiguration(IConfiguration configuration) => new()
    {
        Length = Read(configuration, "Length", 6, 6, 6),
        ExpirationSeconds = Read(configuration, "ExpirationSeconds", 120, 30, 600),
        ResendCooldownSeconds = Read(configuration, "ResendCooldownSeconds", 60, 30, 3600),
        MaxAttempts = Read(configuration, "MaxAttempts", 5, 1, 10),
        MaxRequestsPerHour = Read(configuration, "MaxRequestsPerHour", 5, 1, 20)
    };

    private static int Read(IConfiguration configuration, string name, int fallback, int min, int max)
    {
        var value = configuration.GetValue($"Otp:{name}", fallback);
        if (value < min || value > max)
            throw new InvalidOperationException($"Otp:{name} must be between {min} and {max}.");
        return value;
    }
}
