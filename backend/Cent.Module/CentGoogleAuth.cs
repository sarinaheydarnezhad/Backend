using System.Text.Json;
using Microsoft.EntityFrameworkCore;
namespace Cent.Module;
public static class CentGoogleSettings
{
    public static void ValidateOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) ||
            origin != uri.GetLeftPart(UriPartial.Authority) || !string.IsNullOrEmpty(uri.UserInfo) || origin.Contains('*'))
            throw new InvalidOperationException("Cent:AllowedOrigins must contain exact HTTPS origins (HTTP is allowed only for loopback development).");
    }

    public static CentException Unavailable() => new(503, "google_unavailable", "Google sign-in is unavailable or not configured.");
    public static CentException Invalid() => new(401, "invalid_google_credentials", "Google sign-in could not be verified. Start again.");
}

public sealed record CentGoogleIdentity(string Subject, string Email, string Name);
public static class CentGoogleClaims
{
    public static void ValidateAdditionalClaims(JsonElement claims, string clientId, string nonceHash)
    {
        if (!claims.TryGetProperty("iss", out var issuer) || issuer.ValueKind != JsonValueKind.String ||
            issuer.GetString() is not ("https://accounts.google.com" or "accounts.google.com") ||
            !claims.TryGetProperty("aud", out var audience) || audience.ValueKind != JsonValueKind.String || audience.GetString() != clientId ||
            !claims.TryGetProperty("nonce", out var nonce) || nonce.ValueKind != JsonValueKind.String ||
            CentModule.Hash(nonce.GetString()!) != nonceHash ||
            claims.TryGetProperty("azp", out var azp) && (azp.ValueKind != JsonValueKind.String || azp.GetString() != clientId) ||
            !claims.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number || !exp.TryGetInt64(out var expires) || expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
            !claims.TryGetProperty("iat", out var iat) || iat.ValueKind != JsonValueKind.Number || !iat.TryGetInt64(out var issued) || issued <= 0 || issued >= expires || issued > DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds() ||
            claims.TryGetProperty("nbf", out var nbf) && (nbf.ValueKind != JsonValueKind.Number || !nbf.TryGetInt64(out var notBefore) || notBefore > DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            throw CentGoogleSettings.Invalid();
    }
}
public sealed class CentGoogleAuth(CentDbContext db)
{
    internal async Task<object> AuthenticateIdentity(CentGoogleIdentity identity, CancellationToken ct, Guid? target = null)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.GoogleSubject == identity.Subject, ct);
        if (target is { } id) {
            var owner = await db.Users.SingleAsync(u => u.Id == id, ct);
            if (user is not null && user.Id != id || owner.GoogleSubject is not null && owner.GoogleSubject != identity.Subject) throw Conflict();
            // Conditional assignment prevents concurrent linking from replacing an identity.
            try {
                if (await db.Users.Where(u => u.Id == id && (u.GoogleSubject == null || u.GoogleSubject == identity.Subject))
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.GoogleSubject, identity.Subject), ct) != 1) throw Conflict();
                return await CentAuthEndpoints.StartSession(owner, db, ct);
            }
            catch (DbUpdateException) { throw Conflict(); }
        }
        if (user is null) {
            var normalizedEmail = identity.Email.ToUpperInvariant();
            if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, ct)) throw Conflict();
            user = new CentUser { GoogleSubject = identity.Subject, Email = identity.Email, NormalizedEmail = normalizedEmail, Name = identity.Name };
            db.Users.Add(user);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) {
                // Existing unique subject and email indexes arbitrate concurrent registration.
                db.ChangeTracker.Clear();
                user = await db.Users.SingleOrDefaultAsync(u => u.GoogleSubject == identity.Subject, ct);
                if (user is null) {
                    if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, ct)) throw Conflict();
                    throw;
                }
            }
        }
        // An existing association is authoritative even if Google later changes its email.
        return await CentAuthEndpoints.StartSession(user, db, ct);
    }

    private static CentException Conflict() => new(409, "google_account_conflict", "این هویت به حساب دیگری تعلق دارد. حساب‌ها خودکار ادغام نمی‌شوند.");
}
