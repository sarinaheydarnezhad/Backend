using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Flashcards.Infrastructure.Auth;

public sealed class PhoneAuthService(FlashcardsDbContext database, TokenService tokens,
    IPasswordHasher<User> passwordHasher, AuthSettings settings, ISmsOtpSender sender, TimeProvider clock)
{
    public async Task<OtpRequestResponse> RequestAsync(string phoneNumber, string purpose, CancellationToken cancellationToken = default)
    {
        var phone = PhoneNumber.Normalize(phoneNumber);
        if (purpose is not ("register" or "login"))
            throw new PhoneAuthException(400, "invalid_purpose", "Select registration or login.");
        if (!sender.IsConfigured)
            throw new PhoneAuthException(503, "sms_unavailable", "SMS authentication is not configured.");
        var now = clock.GetUtcNow();
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
        OtpChallenge challenge;
        await using (var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            await database.OtpChallenges.Where(item => item.PhoneNumber == phone)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Attempts, item => item.Attempts), cancellationToken);
            challenge = await database.OtpChallenges.SingleOrDefaultAsync(item => item.PhoneNumber == phone, cancellationToken)
                ?? new OtpChallenge { PhoneNumber = phone, WindowStartedAtUtc = now };
            if (challenge.RequestedAtUtc > now.AddSeconds(-60) ||
                (challenge.WindowStartedAtUtc > now.AddHours(-1) && challenge.RequestsInWindow >= 5))
                throw new PhoneAuthException(429, "otp_rate_limit", "Wait before requesting another code.");
            if (challenge.WindowStartedAtUtc <= now.AddHours(-1))
            {
                challenge.WindowStartedAtUtc = now;
                challenge.RequestsInWindow = 0;
            }
            if (database.Entry(challenge).State == EntityState.Detached) database.OtpChallenges.Add(challenge);
            challenge.Id = Guid.NewGuid();
            challenge.Purpose = purpose;
            challenge.CodeHash = HashCode(challenge.Id, code);
            challenge.RequestedAtUtc = now;
            challenge.RequestsInWindow++;
            challenge.ExpiresAtUtc = now.AddMinutes(5);
            challenge.Attempts = 0;
            challenge.Delivered = false;
            challenge.VerifiedAtUtc = null;
            challenge.ProofHash = null;
            challenge.ProofExpiresAtUtc = null;
            challenge.ConsumedAtUtc = null;
            try { await database.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException)
            {
                throw new PhoneAuthException(429, "otp_rate_limit", "Wait before requesting another code.");
            }
            await transaction.CommitAsync(cancellationToken);
        }
        await sender.SendAsync(phone, code, cancellationToken);
        await database.OtpChallenges.Where(item => item.PhoneNumber == phone && item.Id == challenge.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Delivered, true), cancellationToken);
        return new OtpRequestResponse(challenge.Id, challenge.ExpiresAtUtc, now.AddSeconds(60));
    }

    public async Task<OtpVerificationResponse> VerifyAsync(Guid challengeId, string code, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var claimed = await database.OtpChallenges.Where(item => item.Id == challengeId && item.Delivered &&
                item.ConsumedAtUtc == null && item.VerifiedAtUtc == null && item.Attempts < 5)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Attempts, item => item.Attempts + 1), cancellationToken);
        if (claimed != 1) throw InvalidOtp();
        var challenge = await database.OtpChallenges.AsNoTracking().SingleAsync(item => item.Id == challengeId, cancellationToken);
        if (challenge.ExpiresAtUtc <= clock.GetUtcNow() || code is null || !Regex.IsMatch(code, "^[0-9]{6}$", RegexOptions.CultureInvariant) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(challenge.CodeHash), Convert.FromHexString(HashCode(challengeId, code))))
        {
            await transaction.CommitAsync(cancellationToken);
            throw InvalidOtp();
        }
        if (challenge.Purpose == "login")
        {
            var user = await database.Users.SingleOrDefaultAsync(item => item.PhoneNumber == challenge.PhoneNumber && item.ArchivedAtUtc == null, cancellationToken);
            await database.OtpChallenges.Where(item => item.Id == challengeId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.ConsumedAtUtc, now), cancellationToken);
            if (user is null)
            {
                await transaction.CommitAsync(cancellationToken);
                throw new PhoneAuthException(401, "invalid_credentials", "Unable to sign in. Check your mobile number.");
            }
            var session = await tokens.StartSessionAsync(user, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new OtpVerificationResponse(null, null, session);
        }
        var proof = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var expires = now.AddMinutes(10);
        await database.OtpChallenges.Where(item => item.Id == challengeId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.VerifiedAtUtc, now)
                .SetProperty(item => item.ProofHash, HashProof(proof)).SetProperty(item => item.ProofExpiresAtUtc, expires), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OtpVerificationResponse(proof, expires, null);
    }

    public async Task<AuthTokens> RegisterAsync(string phoneNumber, string registrationToken, string password,
        string confirmPassword, string username, CancellationToken cancellationToken = default)
    {
        var phone = PhoneNumber.Normalize(phoneNumber);
        if (password is null || password.Length is < 12 or > 128 || password != confirmPassword ||
            string.IsNullOrWhiteSpace(username) || username.Trim().Length > 120)
            throw new PhoneAuthException(400, "invalid_registration", "Use a 12–128 character password, matching confirmation, and a username.");
        if (string.IsNullOrEmpty(registrationToken) || registrationToken.Length > 128) throw InvalidOtp();
        var now = clock.GetUtcNow();
        var proofHash = HashProof(registrationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var claimed = await database.OtpChallenges.Where(item => item.PhoneNumber == phone && item.Purpose == "register" &&
                item.ProofHash == proofHash && item.VerifiedAtUtc != null && item.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.ConsumedAtUtc, now), cancellationToken);
        if (claimed != 1) throw InvalidOtp();
        var proofExpiry = await database.OtpChallenges.AsNoTracking().Where(item => item.PhoneNumber == phone)
            .Select(item => item.ProofExpiresAtUtc).SingleAsync(cancellationToken);
        if (proofExpiry is null || proofExpiry <= clock.GetUtcNow()) throw InvalidOtp();
        if (await database.Users.AnyAsync(item => item.PhoneNumber == phone, cancellationToken)) throw DuplicatePhone();
        var user = new User { PhoneNumber = phone, DisplayName = username.Trim() };
        user.PasswordHash = passwordHasher.HashPassword(user, password);
        database.Users.Add(user);
        database.SyncHeads.Add(new SyncHead { UserId = user.Id });
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();
            if (await database.Users.AnyAsync(item => item.PhoneNumber == phone, cancellationToken)) throw DuplicatePhone();
            throw;
        }
        var session = await tokens.StartSessionAsync(user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async Task<AuthTokens> LoginAsync(string phoneNumber, string password, CancellationToken cancellationToken = default)
    {
        var phone = PhoneNumber.Normalize(phoneNumber);
        var user = await database.Users.SingleOrDefaultAsync(item => item.PhoneNumber == phone && item.ArchivedAtUtc == null, cancellationToken);
        if (user?.PasswordHash is null || string.IsNullOrEmpty(password) || password.Length > 128)
            throw new PhoneAuthException(401, "invalid_credentials", "Invalid mobile number or password.");
        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
            throw new PhoneAuthException(401, "invalid_credentials", "Invalid mobile number or password.");
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, password);
            await database.SaveChangesAsync(cancellationToken);
        }
        return await tokens.StartSessionAsync(user, cancellationToken);
    }

    private string HashCode(Guid challengeId, string code) => Convert.ToHexString(
        HMACSHA256.HashData(settings.SigningKey, Encoding.UTF8.GetBytes($"phone-otp:{challengeId:D}:{code}")));
    private static string HashProof(string proof) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proof)));
    private static PhoneAuthException InvalidOtp() => new(401, "invalid_otp", "Code or registration verification is invalid, expired, used, or has too many attempts.");
    private static PhoneAuthException DuplicatePhone() => new(409, "duplicate_phone", "This mobile number already has an account. Please sign in.");
}
