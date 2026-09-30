using System.Security.Claims;

namespace Flashcards.Infrastructure.Auth;

public sealed class AuthService(UserService users, TokenService tokens)
{
    public async Task<AuthTokens?> RegisterAsync(string email, string password, string displayName, CancellationToken cancellationToken = default)
    {
        var user = await users.RegisterAsync(email, password, displayName, cancellationToken);
        return user is null ? null : await tokens.StartSessionAsync(user, cancellationToken);
    }

    public async Task<AuthTokens?> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var user = await users.ValidateCredentialsAsync(email, password, cancellationToken);
        return user is null ? null : await tokens.StartSessionAsync(user, cancellationToken);
    }

    public Task<AuthTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        tokens.RotateAsync(refreshToken, cancellationToken);

    public Task LogoutAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var identity = AuthenticatedIdentity.FromClaims(principal);
        return tokens.RevokeSessionAsync(identity.UserId, identity.SessionId, cancellationToken);
    }
}
