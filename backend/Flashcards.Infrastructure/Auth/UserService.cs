using System.ComponentModel.DataAnnotations;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Auth;

public sealed class UserService(FlashcardsDbContext database, IPasswordHasher<User> passwordHasher)
{
    public async Task<User?> RegisterAsync(string email, string password, string displayName, CancellationToken cancellationToken = default)
    {
        var trimmedEmail = email.Trim();
        var normalizedEmail = trimmedEmail.ToUpperInvariant();
        if (trimmedEmail.Length > 256 || !new EmailAddressAttribute().IsValid(trimmedEmail) ||
            password.Length is < 12 or > 128 || string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 120)
            throw new ArgumentException("Invalid registration details.");

        if (await database.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken))
            return null;

        var newUser = new User { Email = trimmedEmail, NormalizedEmail = normalizedEmail, DisplayName = displayName.Trim() };
        newUser.PasswordHash = passwordHasher.HashPassword(newUser, password);
        database.Users.Add(newUser);
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            database.Entry(newUser).State = EntityState.Detached;
            if (await database.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken))
                return null;
            throw;
        }
        return newUser;
    }

    public async Task<User?> ValidateCredentialsAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password) || email.Length > 256)
            return null;
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var user = await database.Users.SingleOrDefaultAsync(item => item.NormalizedEmail == normalizedEmail && item.ArchivedAtUtc == null, cancellationToken);
        if (user?.PasswordHash is null) return null;

        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed) return null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, password);
            await database.SaveChangesAsync(cancellationToken);
        }
        return user;
    }

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        database.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId && user.ArchivedAtUtc == null, cancellationToken);
}
