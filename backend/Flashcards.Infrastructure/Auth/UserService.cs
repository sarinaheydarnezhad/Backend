using System.ComponentModel.DataAnnotations;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Auth;

public sealed class UserService(FlashcardsDbContext database)
{
    public Task<User?> RegisterAsync(string email, string password, string displayName, CancellationToken cancellationToken = default)
    {
        throw PhoneVerification.Disabled();
    }

    public Task<User?> ValidateCredentialsAsync(string email, string password, CancellationToken cancellationToken = default) =>
        throw PhoneVerification.Disabled();

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        database.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId && user.ArchivedAtUtc == null, cancellationToken);

    public async Task<User?> UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken cancellationToken = default)
    {
        var name = displayName.Trim();
        if (name.Length is < 1 or > 120) throw new ArgumentException("Invalid display name.");
        var user = await database.Users.SingleOrDefaultAsync(item => item.Id == userId && item.ArchivedAtUtc == null, cancellationToken);
        if (user is null) return null;
        user.DisplayName = name;
        await database.SaveChangesAsync(cancellationToken);
        return user;
    }
}
