namespace Flashcards.Infrastructure.Auth;

// Lerona only. Cent uses its independent CentGoogleAuth service and routes.
public sealed class GoogleAuthService
{
    public Task<AuthTokens> SignInAsync(string idToken, CancellationToken cancellationToken = default) =>
        throw PhoneVerification.Disabled();
}
