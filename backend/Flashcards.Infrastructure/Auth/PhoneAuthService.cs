using System.Data;
using System.Security.Cryptography;
using System.Text;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Flashcards.Infrastructure.Auth;

// OTP proves phone ownership for signup or password recovery. It never logs a user in by itself.
public sealed class PhoneAuthService(FlashcardsDbContext database, TokenService tokens,
    AuthSettings settings, ISmsOtpSender sender, TimeProvider clock, OtpSettings otp, IPasswordHasher<User> passwords)
{
    public async Task<OtpRequestResponse> RequestAsync(string phoneNumber, string purpose = "register", CancellationToken cancellationToken = default)
    {
        if (purpose is not ("register" or "reset"))
            throw new PhoneAuthException(400, "invalid_purpose", "SMS verification is available for registration and password recovery.");
        var phone = PhoneNumber.NormalizeIranian(phoneNumber);
        if (!sender.IsConfigured) throw new PhoneAuthException(503, "sms_unavailable", "SMS is temporarily unavailable.");
        var matches = await FindPhoneAsync(phone, cancellationToken);
        if (purpose == "register" && (matches.Count != 0 ||
            await database.Users.AnyAsync(x => x.VerifiedPhoneNumber == phone, cancellationToken))) throw DuplicatePhone();
        if (purpose == "reset" && (matches.Count != 1 || matches[0].ArchivedAtUtc != null)) throw InvalidCredentials();
        var now = clock.GetUtcNow();
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        OtpChallenge challenge;
        await using (var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            await database.OtpChallenges.Where(x => x.PhoneNumber == phone)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Attempts, x => x.Attempts), cancellationToken);
            challenge = await database.OtpChallenges.SingleOrDefaultAsync(x => x.PhoneNumber == phone, cancellationToken)
                ?? new OtpChallenge { PhoneNumber = phone, WindowStartedAtUtc = now };
            var retryAt = challenge.RequestedAtUtc.AddSeconds(otp.ResendCooldownSeconds);
            if (challenge.WindowStartedAtUtc > now.AddHours(-1) && challenge.RequestsInWindow >= otp.MaxRequestsPerHour)
                retryAt = new[] { retryAt, challenge.WindowStartedAtUtc.AddHours(1) }.Max();
            if (retryAt > now) throw new PhoneAuthException(429, "otp_rate_limit", "Wait before requesting another code.",
                (int)Math.Ceiling((retryAt - now).TotalSeconds));
            if (challenge.WindowStartedAtUtc <= now.AddHours(-1)) { challenge.WindowStartedAtUtc = now; challenge.RequestsInWindow = 0; }
            if (database.Entry(challenge).State == EntityState.Detached) database.OtpChallenges.Add(challenge);
            challenge.Id = Guid.NewGuid();
            challenge.Purpose = purpose;
            challenge.ActivationUserId = purpose == "reset" ? matches[0].Id : null;
            challenge.CodeHash = HashCode(challenge, code);
            challenge.RequestedAtUtc = now;
            challenge.RequestsInWindow++;
            challenge.ExpiresAtUtc = now.AddSeconds(otp.ExpirationSeconds);
            challenge.Attempts = 0;
            challenge.Delivered = false;
            challenge.VerifiedAtUtc = null;
            challenge.ProofHash = null;
            challenge.ProofExpiresAtUtc = null;
            challenge.ConsumedAtUtc = null;
            try { await database.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException) { throw new PhoneAuthException(429, "otp_rate_limit", "Wait before requesting another code.", otp.ResendCooldownSeconds); }
            await transaction.CommitAsync(cancellationToken);
        }
        var id = challenge.Id;
        await sender.SendAsync(phone, code, cancellationToken);
        await database.OtpChallenges.Where(x => x.PhoneNumber == phone && x.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Delivered, true), cancellationToken);
        return new OtpRequestResponse(id, challenge.ExpiresAtUtc, now.AddSeconds(otp.ResendCooldownSeconds), 6);
    }

    public async Task<OtpVerificationResponse> VerifyAsync(Guid challengeId, string code, CancellationToken cancellationToken = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var claimed = await database.OtpChallenges.Where(x => x.Id == challengeId &&
                (x.Purpose == "register" || x.Purpose == "reset") && x.Delivered && x.ConsumedAtUtc == null &&
                x.VerifiedAtUtc == null && x.Attempts < otp.MaxAttempts)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);
        if (claimed != 1) throw InvalidOtp();
        var challenge = await database.OtpChallenges.AsNoTracking().SingleAsync(x => x.Id == challengeId, cancellationToken);
        var now = clock.GetUtcNow();
        if (challenge.ExpiresAtUtc <= now || code is null || code.Length != 6 || code.Any(c => !char.IsAsciiDigit(c)) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(challenge.CodeHash), Convert.FromHexString(HashCode(challenge, code))))
        {
            await transaction.CommitAsync(cancellationToken);
            throw InvalidOtp();
        }
        if (challenge.Purpose == "reset" && !await ResetOwnerExists(challenge, cancellationToken)) throw InvalidOtp();
        var proof = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var expires = now.AddMinutes(10);
        await database.OtpChallenges.Where(x => x.Id == challengeId).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.VerifiedAtUtc, now).SetProperty(x => x.ProofHash, HashProof(proof))
            .SetProperty(x => x.ProofExpiresAtUtc, expires), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return challenge.Purpose == "register"
            ? new OtpVerificationResponse(proof, expires, null)
            : new OtpVerificationResponse(null, expires, null, proof);
    }

    public async Task<AuthTokens> RegisterAsync(string phoneNumber, string registrationToken, string password,
        string confirmPassword, string username, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password, confirmPassword);
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length > 120)
            throw new PhoneAuthException(400, "invalid_username", "Enter a name of 1–120 characters.");
        var phone = PhoneNumber.NormalizeIranian(phoneNumber);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ConsumeProof(phone, registrationToken, "register", cancellationToken);
        if ((await FindPhoneAsync(phone, cancellationToken)).Count != 0 ||
            await database.Users.AnyAsync(x => x.VerifiedPhoneNumber == phone, cancellationToken)) throw DuplicatePhone();
        var user = new User { PhoneNumber = phone, VerifiedPhoneNumber = phone,
            PhoneVerifiedAtUtc = clock.GetUtcNow(), DisplayName = username.Trim() };
        user.PasswordHash = passwords.HashPassword(user, password);
        database.Users.Add(user);
        database.SyncHeads.Add(new SyncHead { UserId = user.Id });
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            database.ChangeTracker.Clear();
            if ((await FindPhoneAsync(phone, cancellationToken)).Count != 0) throw DuplicatePhone();
            throw;
        }
        var session = await tokens.StartPasswordSessionAsync(user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async Task<AuthTokens> LoginAsync(string phoneNumber, string password, CancellationToken cancellationToken = default)
    {
        var phone = PhoneNumber.NormalizeIranian(phoneNumber);
        if (string.IsNullOrEmpty(password) || password.Length > 128) throw InvalidCredentials();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var matches = await FindPhoneAsync(phone, cancellationToken);
        if (matches.Count != 1 || matches[0].ArchivedAtUtc != null) throw InvalidCredentials();
        var id = matches[0].Id;
        // Serialize password verification/session issuance with password reset and its revocations.
        await LockUser(id, cancellationToken);
        var user = await database.Users.SingleAsync(x => x.Id == id, cancellationToken);
        if (user.ArchivedAtUtc != null || user.PasswordHash is null) throw InvalidCredentials();
        var result = passwords.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed) throw InvalidCredentials();
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwords.HashPassword(user, password);
            await database.SaveChangesAsync(cancellationToken);
        }
        var session = await tokens.StartPasswordSessionAsync(user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async Task<AuthTokens> ResetPasswordAsync(string phoneNumber, string resetToken, string password,
        string confirmPassword, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password, confirmPassword);
        var phone = PhoneNumber.NormalizeIranian(phoneNumber);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var challenge = await ConsumeProof(phone, resetToken, "reset", cancellationToken);
        if (!await ResetOwnerExists(challenge, cancellationToken)) throw InvalidOtp();
        var id = challenge.ActivationUserId!.Value;
        await LockUser(id, cancellationToken);
        var user = await database.Users.SingleAsync(x => x.Id == id, cancellationToken);
        if (user.ArchivedAtUtc != null || (user.PhoneNumber != phone && user.PhoneNumber != LocalPhone(phone))) throw InvalidOtp();
        user.PasswordHash = passwords.HashPassword(user, password);
        user.PhoneNumber = phone;
        user.VerifiedPhoneNumber = phone;
        user.PhoneVerifiedAtUtc = clock.GetUtcNow();
        await database.SaveChangesAsync(cancellationToken);
        await database.AuthSessions.Where(x => x.UserId == id && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.RevokedAtUtc, clock.GetUtcNow()), cancellationToken);
        var session = await tokens.StartPasswordSessionAsync(user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    private async Task<OtpChallenge> ConsumeProof(string phone, string proof, string purpose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(proof) || proof.Length > 128) throw InvalidOtp();
        var hash = HashProof(proof);
        var claimed = await database.OtpChallenges.Where(x => x.PhoneNumber == phone && x.Purpose == purpose &&
                x.Delivered && x.ProofHash == hash && x.VerifiedAtUtc != null && x.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ConsumedAtUtc, clock.GetUtcNow()), ct);
        if (claimed != 1) throw InvalidOtp();
        var challenge = await database.OtpChallenges.AsNoTracking().SingleAsync(x => x.PhoneNumber == phone, ct);
        if (challenge.ProofExpiresAtUtc is null || challenge.ProofExpiresAtUtc <= clock.GetUtcNow()) throw InvalidOtp();
        return challenge;
    }

    private Task<int> LockUser(Guid id, CancellationToken ct) => database.Users.Where(x => x.Id == id && x.ArchivedAtUtc == null)
        .ExecuteUpdateAsync(u => u.SetProperty(x => x.PasswordHash, x => x.PasswordHash), ct);
    private Task<List<User>> FindPhoneAsync(string phone, CancellationToken ct) => database.Users.AsNoTracking()
        .Where(x => x.PhoneNumber == phone || x.PhoneNumber == LocalPhone(phone)).ToListAsync(ct);
    private Task<bool> ResetOwnerExists(OtpChallenge challenge, CancellationToken ct) => database.Users.AnyAsync(x =>
        x.Id == challenge.ActivationUserId && x.ArchivedAtUtc == null &&
        (x.PhoneNumber == challenge.PhoneNumber || x.PhoneNumber == LocalPhone(challenge.PhoneNumber)), ct);
    private static string LocalPhone(string phone) => "0" + phone[3..];
    private string HashCode(OtpChallenge challenge, string code) => Convert.ToHexString(HMACSHA256.HashData(settings.SigningKey,
        Encoding.UTF8.GetBytes($"lerona:{challenge.Purpose}:{challenge.Id:D}:{challenge.PhoneNumber}:{challenge.ActivationUserId}:{code}")));
    private static string HashProof(string proof) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proof)));
    private static void ValidatePassword(string password, string confirmation)
    {
        if (!PhonePasswordPolicy.IsValid(password))
            throw new PhoneAuthException(400, "invalid_password", "Use 8–128 characters with letters and numbers; avoid common passwords.");
        if (password != confirmation) throw new PhoneAuthException(400, "password_mismatch", "The passwords do not match.");
    }
    private static PhoneAuthException InvalidOtp() => new(401, "invalid_otp", "Verification is invalid, expired or already used.");
    private static PhoneAuthException InvalidCredentials() => new(401, "invalid_credentials", "Invalid mobile number or password.");
    private static PhoneAuthException DuplicatePhone() => new(409, "duplicate_phone", "This mobile number already has an account. Please sign in.");
}
