using System.Data;
using System.Security.Cryptography;
using System.Text;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Auth;

public sealed class PhoneAuthService(FlashcardsDbContext database, TokenService tokens,
    AuthSettings settings, ISmsOtpSender sender, TimeProvider clock, OtpSettings otp)
{
    public async Task<OtpRequestResponse> RequestAsync(string phoneNumber, string purpose = "authenticate", CancellationToken cancellationToken = default)
    {
        if (purpose != "authenticate")
            throw new PhoneAuthException(400, "invalid_purpose", "درخواست ورود معتبر نیست.");
        if (otp.Length != 6) throw new InvalidOperationException("Lerona OTP length must be six.");
        var phone = PhoneNumber.NormalizeIranian(phoneNumber);
        if (!sender.IsConfigured) throw new PhoneAuthException(503, "sms_unavailable", "سرویس پیامک در دسترس نیست.");
        var now = clock.GetUtcNow();
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        OtpChallenge challenge;
        await using (var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            // This update lock serializes requests with requests and verification for this phone.
            await database.OtpChallenges.Where(item => item.PhoneNumber == phone)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Attempts, item => item.Attempts), cancellationToken);
            challenge = await database.OtpChallenges.SingleOrDefaultAsync(item => item.PhoneNumber == phone, cancellationToken)
                ?? new OtpChallenge { PhoneNumber = phone, WindowStartedAtUtc = now };
            var retryAt = challenge.RequestedAtUtc.AddSeconds(otp.ResendCooldownSeconds);
            if (challenge.WindowStartedAtUtc > now.AddHours(-1) && challenge.RequestsInWindow >= otp.MaxRequestsPerHour)
                retryAt = new[] { retryAt, challenge.WindowStartedAtUtc.AddHours(1) }.Max();
            if (retryAt > now)
                throw new PhoneAuthException(429, "otp_rate_limit", "برای دریافت کد جدید کمی صبر کنید.", (int)Math.Ceiling((retryAt - now).TotalSeconds));
            if (challenge.WindowStartedAtUtc <= now.AddHours(-1)) { challenge.WindowStartedAtUtc = now; challenge.RequestsInWindow = 0; }
            if (database.Entry(challenge).State == EntityState.Detached) database.OtpChallenges.Add(challenge);
            challenge.Id = Guid.NewGuid();
            challenge.Purpose = "authenticate";
            challenge.ActivationUserId = null;
            challenge.CodeHash = HashCode(challenge.Id, phone, code);
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
            catch (DbUpdateException) { throw new PhoneAuthException(429, "otp_rate_limit", "برای دریافت کد جدید کمی صبر کنید.", otp.ResendCooldownSeconds); }
            await transaction.CommitAsync(cancellationToken);
        }
        var id = challenge.Id;
        await sender.SendAsync(phone, code, cancellationToken);
        await database.OtpChallenges.Where(item => item.PhoneNumber == phone && item.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Delivered, true), cancellationToken);
        return new OtpRequestResponse(id, challenge.ExpiresAtUtc, now.AddSeconds(otp.ResendCooldownSeconds), 6);
    }

    public async Task<OtpVerificationResponse> VerifyAsync(Guid challengeId, string code, CancellationToken cancellationToken = default)
    {
        // The first write locks the challenge before reading; only one verifier can consume it.
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var claimed = await database.OtpChallenges.Where(item => item.Id == challengeId && item.Purpose == "authenticate" && item.Delivered &&
                item.ConsumedAtUtc == null && item.VerifiedAtUtc == null && item.Attempts < otp.MaxAttempts)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Attempts, item => item.Attempts + 1), cancellationToken);
        if (claimed != 1) throw InvalidOtp();
        var challenge = await database.OtpChallenges.AsNoTracking().SingleAsync(item => item.Id == challengeId, cancellationToken);
        var now = clock.GetUtcNow();
        if (challenge.ExpiresAtUtc <= now || code is null || code.Length != 6 || code.Any(c => !char.IsAsciiDigit(c)) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(challenge.CodeHash), Convert.FromHexString(HashCode(challengeId, challenge.PhoneNumber, code))))
        {
            await transaction.CommitAsync(cancellationToken);
            throw InvalidOtp();
        }
        await database.OtpChallenges.Where(item => item.Id == challengeId).ExecuteUpdateAsync(update => update
            .SetProperty(item => item.VerifiedAtUtc, now).SetProperty(item => item.ConsumedAtUtc, now), cancellationToken);
        var matches = await database.Users.Where(item => item.PhoneNumber == challenge.PhoneNumber ||
            item.PhoneNumber == "0" + challenge.PhoneNumber.Substring(3) || item.VerifiedPhoneNumber == challenge.PhoneNumber).ToListAsync(cancellationToken);
        User user;
        if (matches.Count != 0)
        {
            if (matches.Count != 1 || matches[0].ArchivedAtUtc != null || !PhoneVerification.IsVerified(matches[0]) || matches[0].PhoneNumber != challenge.PhoneNumber)
            {
                await transaction.CommitAsync(cancellationToken);
                throw RecoveryRequired();
            }
            user = matches[0];
        }
        else
        {
            // An unknown phone cannot distinguish a new user from a legacy email/Google user.
            // Close signup until staff resolve the legacy ownership inventory.
            if (await database.Users.AnyAsync(item => item.ArchivedAtUtc == null &&
                (item.PhoneNumber == null || item.PhoneNumber.Length != 13 || !item.PhoneNumber.StartsWith("+989") ||
                 item.PhoneVerifiedAtUtc == null || item.VerifiedPhoneNumber == null || item.VerifiedPhoneNumber != item.PhoneNumber), cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                throw RecoveryRequired();
            }
            user = new User { PhoneNumber = challenge.PhoneNumber, VerifiedPhoneNumber = challenge.PhoneNumber,
                PhoneVerifiedAtUtc = now, DisplayName = "کاربر لرونا" };
            database.Users.Add(user);
            database.SyncHeads.Add(new SyncHead { UserId = user.Id });
            await database.SaveChangesAsync(cancellationToken);
        }
        var session = await tokens.StartOtpSessionAsync(user, challengeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OtpVerificationResponse(null, null, session);
    }

    private string HashCode(Guid id, string phone, string code) => Convert.ToHexString(HMACSHA256.HashData(settings.SigningKey,
        Encoding.UTF8.GetBytes($"lerona:authenticate:{id:D}:{phone}:{code}")));
    private static PhoneAuthException InvalidOtp() => new(401, "invalid_otp", "کد تأیید نامعتبر، منقضی یا قبلاً استفاده شده است.");
    private static PhoneAuthException RecoveryRequired() => new(409, "account_recovery_required", "برای حفظ اطلاعات حساب قبلی، بازیابی امن از طریق پشتیبانی لازم است.");
}
