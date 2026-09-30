using Microsoft.Extensions.Configuration;

namespace Flashcards.Infrastructure.Auth;

public sealed class AuthSettings
{
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required byte[] SigningKey { get; init; }

    public static AuthSettings FromConfiguration(IConfiguration configuration)
    {
        var issuer = configuration["Auth:Issuer"];
        var audience = configuration["Auth:Audience"];
        var encodedKey = configuration["Auth:SigningKey"];
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(encodedKey))
            throw new InvalidOperationException("Auth:Issuer, Auth:Audience, and Auth:SigningKey must be configured.");

        byte[] key;
        try { key = Convert.FromBase64String(encodedKey); }
        catch (FormatException exception) { throw new InvalidOperationException("Auth:SigningKey must be base64 encoded.", exception); }
        if (key.Length < 32)
            throw new InvalidOperationException("Auth:SigningKey must contain at least 32 random bytes.");

        return new AuthSettings { Issuer = issuer, Audience = audience, SigningKey = key };
    }
}
