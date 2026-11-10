using Flashcards.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;

namespace Flashcards.Tests;

public sealed class OtpSettingsTests
{
    [Fact]
    public void Defaults_match_the_two_minute_six_digit_policy()
    {
        var options = OtpSettings.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Equal(6, options.Length);
        Assert.Equal(120, options.ExpirationSeconds);
        Assert.Equal(60, options.ResendCooldownSeconds);
        Assert.Equal(5, options.MaxAttempts);
        Assert.Equal(5, options.MaxRequestsPerHour);
    }

    [Theory]
    [InlineData("Length", "5")]
    [InlineData("Length", "7")]
    [InlineData("Length", "8")]
    [InlineData("ExpirationSeconds", "0")]
    [InlineData("MaxAttempts", "100")]
    [InlineData("ResendCooldownSeconds", "0")]
    [InlineData("MaxRequestsPerHour", "0")]
    public void Unsafe_policy_is_rejected(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Otp:{key}"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => OtpSettings.FromConfiguration(config));
    }

    [Theory]
    [InlineData("09121234567")]
    [InlineData("+989121234567")]
    [InlineData("۰۹۱۲۱۲۳۴۵۶۷")]
    [InlineData("٠٩١٢١٢٣٤٥٦٧")]
    public void Iranian_formats_normalize_to_one_identity(string phone) => Assert.Equal("+989121234567", PhoneNumber.NormalizeIranian(phone));

    [Theory]
    [InlineData("02121234567")]
    [InlineData("+982112345678")]
    [InlineData("+14155552671")]
    [InlineData("0912 1234567")]
    [InlineData("09121234567\nextra")]
    public void Otp_rejects_non_mobile_and_malformed_numbers(string phone) => Assert.Throws<PhoneAuthException>(() => PhoneNumber.NormalizeIranian(phone));
}
