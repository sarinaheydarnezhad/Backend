using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Flashcards.Infrastructure.Auth;

public sealed record GoogleIdentity(string Subject, string Email, string DisplayName);

public interface IGoogleTokenVerifier
{
    Task<GoogleIdentity> VerifyAsync(string idToken, CancellationToken cancellationToken = default);
}

public sealed class GoogleTokenVerifier(IConfiguration configuration,
    IConfigurationManager<OpenIdConnectConfiguration> metadata) : IGoogleTokenVerifier
{
    public async Task<GoogleIdentity> VerifyAsync(string idToken, CancellationToken cancellationToken = default)
    {
        var clientId = configuration["Google:WebClientId"]?.Trim();
        if (string.IsNullOrEmpty(clientId)) throw GoogleAuthException.Unavailable();
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > 8192) throw GoogleAuthException.InvalidToken();
        OpenIdConnectConfiguration discovery;
        try { discovery = await metadata.GetConfigurationAsync(cancellationToken); }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw GoogleAuthException.Unavailable();
        }

        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 8192 };
            var principal = handler.ValidateToken(idToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
                ValidateAudience = true,
                ValidAudience = clientId,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = discovery.SigningKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                IncludeTokenOnFailedValidation = false,
                LogValidationExceptions = false
            }, out _);
            var subject = principal.FindFirst("sub")?.Value;
            var email = principal.FindFirst("email")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255 ||
                string.IsNullOrEmpty(email) || email.Length > 256 || !new EmailAddressAttribute().IsValid(email) ||
                !string.Equals(principal.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                throw GoogleAuthException.InvalidToken();
            var name = principal.FindFirst("name")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = email.Split('@')[0];
            return new GoogleIdentity(subject, email, name.Length > 120 ? name[..120] : name);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            metadata.RequestRefresh();
            throw GoogleAuthException.InvalidToken();
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            throw GoogleAuthException.InvalidToken();
        }
    }
}

public sealed class GoogleAuthException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public static GoogleAuthException InvalidToken() => new(401, "invalid_google_token", "Google sign-in could not be verified. Please try again.");
    public static GoogleAuthException Unavailable() => new(503, "google_unavailable", "Google sign-in is unavailable or not configured.");
}
