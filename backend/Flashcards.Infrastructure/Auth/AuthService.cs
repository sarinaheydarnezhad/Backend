using System.Security.Claims;

namespace Flashcards.Infrastructure.Auth;

public sealed class AuthService(TokenService tokens)
{
    public Task<AuthTokens?> RegisterAsync(string email, string password, string displayName, CancellationToken cancellationToken = default,
        string? phoneNumber = null, string? registrationToken = null)
    {
        throw PhoneVerification.Disabled();
    }

    public Task<AuthTokens?> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        throw PhoneVerification.Disabled();
    }

    public Task<AuthTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        tokens.RotateAsync(refreshToken, cancellationToken);

    public Task LogoutAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var identity = AuthenticatedIdentity.FromClaims(principal);
        return tokens.RevokeSessionAsync(identity.UserId, identity.SessionId, cancellationToken);
    }

    public Task LogoutSessionAsync(ClaimsPrincipal principal, Guid sessionId, CancellationToken cancellationToken = default) =>
        tokens.RevokeSessionAsync(AuthenticatedIdentity.FromClaims(principal).UserId, sessionId, cancellationToken);
}
