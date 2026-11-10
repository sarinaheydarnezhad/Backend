using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Flashcards.Infrastructure.Auth;

public sealed record AuthTokens(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAtUtc, Guid SessionId, DateTimeOffset SessionExpiresAtUtc, bool PhoneVerified = true);

public sealed class TokenService(FlashcardsDbContext database, AuthSettings settings)
{
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);

    internal async Task<AuthTokens> StartOtpSessionAsync(User user, Guid challengeId, CancellationToken cancellationToken = default)
    {
        if (database.Database.CurrentTransaction is null || !await database.OtpChallenges.AnyAsync(item =>
            item.Id == challengeId && item.Purpose == "authenticate" && item.PhoneNumber == user.PhoneNumber &&
            item.VerifiedAtUtc != null && item.ConsumedAtUtc != null && item.Delivered, cancellationToken))
            throw PhoneVerification.Required();
        await using var transaction = database.Database.CurrentTransaction is null
            ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
        // Lock the durable user row; never trust a detached/client-supplied verification flag.
        if (!await LockVerifiedUserAsync(user.Id, cancellationToken)) throw PhoneVerification.Required();
        var now = DateTimeOffset.UtcNow;
        var session = new AuthSession { UserId = user.Id, ExpiresAtUtc = now.Add(RefreshLifetime), OtpAuthenticated = true };
        var rawRefreshToken = CreateRefreshToken(session, now);
        database.AuthSessions.Add(session);
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return CreateTokens(user.Id, session.Id, rawRefreshToken, now, session.ExpiresAtUtc);
    }

    public async Task<AuthTokens?> RotateAsync(string rawRefreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken) || rawRefreshToken.Length > 256)
            return null;

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var hash = HashToken(rawRefreshToken);
            var token = await database.RefreshTokens.AsNoTracking()
                .Include(item => item.AuthSession).ThenInclude(session => session.User)
                .SingleOrDefaultAsync(item => item.TokenHash == hash, cancellationToken);
            if (token is null) return null;

            var now = DateTimeOffset.UtcNow;
            var session = token.AuthSession;
            if (token.ConsumedAtUtc is not null)
            {
                await RevokeSessionAsync(session.UserId, session.Id, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            if (!session.OtpAuthenticated || session.RevokedAtUtc is not null || session.ExpiresAtUtc <= now || token.ExpiresAtUtc <= now || session.User.ArchivedAtUtc is not null)
                return null;
            if (!await LockVerifiedUserAsync(session.UserId, cancellationToken)) return null;

            var claimed = await database.RefreshTokens
                .Where(item => item.Id == token.Id && item.ConsumedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.ConsumedAtUtc, now), cancellationToken);
            if (claimed != 1)
            {
                await RevokeSessionAsync(session.UserId, session.Id, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            var newRefreshToken = CreateRefreshToken(session, now);
            await database.SaveChangesAsync(cancellationToken);
            if (!await IsSessionActiveAsync(session.UserId, session.Id, cancellationToken))
                return null;
            await transaction.CommitAsync(cancellationToken);
            return CreateTokens(session.UserId, session.Id, newRefreshToken, now, session.ExpiresAtUtc);
        }
        catch
        {
            database.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default) =>
        await database.AuthSessions.Where(session => session.Id == sessionId && session.UserId == userId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAtUtc, DateTimeOffset.UtcNow), cancellationToken);

    public async Task<bool> IsSessionActiveAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await database.AuthSessions.AsNoTracking()
            .Where(item => item.OtpAuthenticated && item.Id == sessionId && item.UserId == userId && item.RevokedAtUtc == null && item.User.ArchivedAtUtc == null &&
                item.User.PhoneVerifiedAtUtc != null && item.User.PhoneNumber != null && item.User.VerifiedPhoneNumber == item.User.PhoneNumber)
            .Select(item => new { item.ExpiresAtUtc })
            .SingleOrDefaultAsync(cancellationToken);
        return session is not null && session.ExpiresAtUtc > DateTimeOffset.UtcNow;
    }

    private async Task<bool> LockVerifiedUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await database.Users.Where(user => user.Id == userId && user.ArchivedAtUtc == null &&
            user.PhoneNumber != null && user.PhoneVerifiedAtUtc != null && user.VerifiedPhoneNumber == user.PhoneNumber)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.PhoneVerifiedAtUtc, user => user.PhoneVerifiedAtUtc), cancellationToken) == 1;

    private string CreateRefreshToken(AuthSession session, DateTimeOffset now)
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        database.RefreshTokens.Add(new RefreshToken
        {
            AuthSessionId = session.Id, TokenHash = HashToken(raw), ExpiresAtUtc = session.ExpiresAtUtc,
            CreatedAtUtc = now
        });
        return raw;
    }

    private AuthTokens CreateTokens(Guid userId, Guid sessionId, string refreshToken, DateTimeOffset now, DateTimeOffset sessionExpiresAtUtc)
    {
        var expires = now.Add(AccessLifetime);
        var jwt = new JwtSecurityToken(
            issuer: settings.Issuer, audience: settings.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString("D")),
                new Claim("sid", sessionId.ToString("D")),
                new Claim("phone_verified", "true"),
                new Claim("amr", "sms_otp"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D"))
            ],
            notBefore: now.UtcDateTime, expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(settings.SigningKey), SecurityAlgorithms.HmacSha256));
        return new AuthTokens(new JwtSecurityTokenHandler().WriteToken(jwt), refreshToken, expires, sessionId, sessionExpiresAtUtc);
    }

    private static string HashToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
