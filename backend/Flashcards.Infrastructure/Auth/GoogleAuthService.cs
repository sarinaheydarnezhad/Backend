using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Auth;

public sealed class GoogleAuthService(FlashcardsDbContext database, TokenService tokens, IGoogleTokenVerifier verifier)
{
    public async Task<AuthTokens> SignInAsync(string idToken, CancellationToken cancellationToken = default)
    {
        var identity = await verifier.VerifyAsync(idToken, cancellationToken);
        var user = await database.Users.SingleOrDefaultAsync(item => item.GoogleSubject == identity.Subject, cancellationToken);
        if (user is not null)
        {
            if (user.ArchivedAtUtc is not null) throw GoogleAuthException.InvalidToken();
            return await tokens.StartSessionAsync(user, cancellationToken);
        }

        var normalizedEmail = identity.Email.ToUpperInvariant();
        if (await database.Users.AnyAsync(item => item.NormalizedEmail == normalizedEmail, cancellationToken))
            throw AccountConflict();
        user = new User
        {
            GoogleSubject = identity.Subject,
            Email = identity.Email,
            NormalizedEmail = normalizedEmail,
            DisplayName = identity.DisplayName
        };
        database.Users.Add(user);
        database.SyncHeads.Add(new SyncHead { UserId = user.Id });
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            var existing = await database.Users.SingleOrDefaultAsync(item => item.GoogleSubject == identity.Subject, cancellationToken);
            if (existing is not null && existing.ArchivedAtUtc is null)
                return await tokens.StartSessionAsync(existing, cancellationToken);
            if (existing is not null) throw GoogleAuthException.InvalidToken();
            if (await database.Users.AnyAsync(item => item.NormalizedEmail == normalizedEmail, cancellationToken))
                throw AccountConflict();
            throw;
        }
        return await tokens.StartSessionAsync(user, cancellationToken);
    }

    private static GoogleAuthException AccountConflict() => new(409, "google_account_conflict",
        "This email already belongs to an account. Sign in using its existing method.");
}
