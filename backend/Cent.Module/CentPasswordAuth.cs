using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cent.Module;

public sealed record CentPasswordLogin(string Phone, string Password)
{
    public override string ToString() => "Cent password login (redacted)";
}

public sealed partial class CentSmsAuth
{
    private static readonly PasswordHasher<CentUser> PasswordHasher = new();
    private static readonly Lazy<string> DummyPasswordHash = new(() =>
        PasswordHasher.HashPassword(new CentUser(), "Cent missing password credential"));

    private static void ValidateNewPassword(string? password) =>
        CentModule.Check(password is { Length: >= 15 and <= 128 } && !string.IsNullOrWhiteSpace(password),
            "رمز عبور باید بین ۱۵ تا ۱۲۸ نویسه باشد.", 400, "invalid_password");

    public async Task<object> SignInWithPassword(CentPasswordLogin input, string ip, CancellationToken ct)
    {
        var phone = CentIranPhone.Normalize(input.Phone);
        var key = Key();
        var now = clock.GetUtcNow();
        CentModule.Check(input.Password is { Length: > 0 and <= 128 }, "رمز عبور معتبر وارد کنید.", 400, "invalid_password");
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)) {
            await Budget("password-ip:" + Mac(key, ip), 30, 900, 0, now, ct);
            await Budget("password-phone:" + Mac(key, phone), 10, 900, 0, now, ct);
            await db.Set<CentSmsBudget>().Where(b => b.ResetAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var user = await (from identity in db.Set<CentMobileIdentity>().AsNoTracking()
                          join account in db.Users.AsNoTracking() on identity.UserId equals account.Id
                          where identity.Phone == phone
                          select account).SingleOrDefaultAsync(ct);
        var stored = string.IsNullOrEmpty(user?.PasswordHash) ? null : user.PasswordHash;
        var result = PasswordHasher.VerifyHashedPassword(user ?? new CentUser(), stored ?? DummyPasswordHash.Value, input.Password);
        if (user is null || stored is null || result == PasswordVerificationResult.Failed) throw InvalidCredentials();

        // Recheck the credential in the session transaction so a concurrent reset cannot revive an old password.
        await using var sessionTx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var current = await db.Users.SingleAsync(u => u.Id == user.Id, ct);
        if (current.PasswordHash != stored) throw InvalidCredentials();
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            current.PasswordHash = PasswordHasher.HashPassword(current, input.Password);
        var session = await CentAuthEndpoints.StartSession(current, db, ct);
        await sessionTx.CommitAsync(ct);
        return session;
    }

    private async Task ResetPassword(Guid userId, string password, DateTimeOffset now, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        user.PasswordHash = PasswordHasher.HashPassword(user, password);
        await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.RevokedAt, now), ct);
        await db.SaveChangesAsync(ct);
    }

    private static CentException InvalidCredentials() =>
        new(401, "invalid_credentials", "شماره موبایل یا رمز عبور درست نیست.");
}
