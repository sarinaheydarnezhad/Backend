using System.Net.Mail;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Cent.Module;

public sealed record CentGoogleSettings(string ClientId, string ClientSecret, string RedirectUri)
{
    public override string ToString() => "Cent Google OAuth settings (credentials redacted)";
    public static CentGoogleSettings Read(IConfiguration config, IHostEnvironment environment)
    {
        if (!config.GetValue<bool>("Cent:Google:Enabled")) throw Unavailable();
        var id = config["Cent:Google:ClientId"]?.Trim();
        var secret = config["Cent:Google:ClientSecret"];
        var redirect = config["Cent:Google:RedirectUri"];
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) ||
            !Uri.TryCreate(redirect, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !(uri.Scheme == "https" || environment.IsDevelopment() && uri.Scheme == "http" && uri.IsLoopback) ||
            !(config.GetSection("Cent:AllowedOrigins").Get<string[]>() ?? []).Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal))
            throw Unavailable();
        return new(id, secret, redirect!);
    }

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
public sealed record CentGoogleStart(string AuthorizationUrl, string State, string CodeVerifier, DateTimeOffset ExpiresAt);
public sealed record CentGoogleComplete(string State, string CodeVerifier, string? Code, string? Error);

public interface ICentGoogleClient
{
    Task<CentGoogleIdentity> ExchangeAndValidate(CentGoogleSettings settings, string code, string verifier, string nonceHash, CancellationToken ct);
}

public sealed class CentGoogleClient(HttpClient http) : ICentGoogleClient
{
    public async Task<CentGoogleIdentity> ExchangeAndValidate(CentGoogleSettings settings, string code, string verifier, string nonceHash, CancellationToken ct)
    {
        try {
            using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string> {
                ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = verifier,
                ["client_id"] = settings.ClientId, ["client_secret"] = settings.ClientSecret, ["redirect_uri"] = settings.RedirectUri
            }), ct);
            if (!response.IsSuccessStatusCode) {
                if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429) throw CentGoogleSettings.Unavailable();
                throw CentGoogleSettings.Invalid();
            }
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
            if (body is null || !body.RootElement.TryGetProperty("id_token", out var value) || value.ValueKind != JsonValueKind.String)
                throw CentGoogleSettings.Invalid();
            return await ValidateToken(value.GetString()!, settings.ClientId, nonceHash, ct);
        }
        catch (JsonException) { throw CentGoogleSettings.Invalid(); }
        catch (HttpRequestException) { throw CentGoogleSettings.Unavailable(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw CentGoogleSettings.Unavailable(); }
    }

    public static async Task<CentGoogleIdentity> ValidateToken(string token, string clientId, string nonceHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 10000) throw CentGoogleSettings.Invalid();
        try {
            // Google's official validator checks RS256 signature against Google's rotating keys,
            // both Google issuers, exact audience, issued-at and expiry (no expiry grace).
            var payload = await GoogleJsonWebSignature.ValidateAsync(token, new GoogleJsonWebSignature.ValidationSettings {
                Audience = [clientId], ExpirationTimeClockTolerance = TimeSpan.Zero,
                IssuedAtClockTolerance = TimeSpan.FromSeconds(30)
            }).WaitAsync(TimeSpan.FromSeconds(15), ct);
            using var claims = JsonDocument.Parse(WebEncoders.Base64UrlDecode(token.Split('.')[1]));
            ValidateAdditionalClaims(claims.RootElement, clientId, nonceHash);
            if (string.IsNullOrWhiteSpace(payload.Subject) || payload.Subject.Length > 255 || !payload.EmailVerified ||
                string.IsNullOrWhiteSpace(payload.Email) || payload.Email.Length > 256 ||
                !MailAddress.TryCreate(payload.Email, out var email) || email.Address != payload.Email)
                throw CentGoogleSettings.Invalid();
            var name = string.IsNullOrWhiteSpace(payload.Name) ? payload.Email.Split('@')[0] : payload.Name.Trim();
            return new(payload.Subject, payload.Email, name.Length > 120 ? name[..120] : name);
        }
        catch (InvalidJwtException) { throw CentGoogleSettings.Invalid(); }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException) { throw CentGoogleSettings.Invalid(); }
        catch (Exception e) when (e is HttpRequestException or TimeoutException) { throw CentGoogleSettings.Unavailable(); }
    }

    // Called only after signature validation. Explicitly bind the ID token to this login attempt.
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

public sealed class CentGoogleAuth(CentDbContext db, IConfiguration config, IHostEnvironment environment, ICentGoogleClient google)
{
    public async Task<CentGoogleStart> Start(CancellationToken ct)
    {
        var settings = CentGoogleSettings.Read(config, environment);
        var state = Random(); var verifier = Random(); var nonce = Random();
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddMinutes(5);
        await db.GoogleAttempts.Where(a => a.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        db.GoogleAttempts.Add(new CentGoogleAttempt { StateHash = CentModule.Hash(state), VerifierHash = CentModule.Hash(verifier), NonceHash = CentModule.Hash(nonce), ExpiresAt = expiry });
        await db.SaveChangesAsync(ct);
        var url = QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", new Dictionary<string, string?> {
            ["client_id"] = settings.ClientId, ["redirect_uri"] = settings.RedirectUri, ["response_type"] = "code",
            ["scope"] = "openid email profile", ["state"] = state, ["nonce"] = nonce,
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256", ["prompt"] = "select_account"
        });
        return new(url, state, verifier, expiry);
    }

    public async Task<object> Complete(CentGoogleComplete input, CancellationToken ct)
    {
        var settings = CentGoogleSettings.Read(config, environment);
        if (input.State is not { Length: 43 } || input.CodeVerifier is not { Length: 43 } ||
            input.Code?.Length > 4096 || input.Error?.Length > 256) throw CentGoogleSettings.Invalid();
        var hash = CentModule.Hash(input.State); var proof = CentModule.Hash(input.CodeVerifier);
        var now = DateTimeOffset.UtcNow;
        var pending = db.GoogleAttempts.Where(a => a.StateHash == hash && a.VerifierHash == proof && a.ExpiresAt > now);
        var attempt = await pending.AsNoTracking().SingleOrDefaultAsync(ct);
        var consumedAt = DateTimeOffset.UtcNow;
        if (attempt is null || await pending.Where(a => a.ExpiresAt > consumedAt).ExecuteDeleteAsync(ct) != 1) throw CentGoogleSettings.Invalid();
        // Consume before external calls: concurrent callbacks/replays cannot create extra sessions.
        if (!string.IsNullOrEmpty(input.Error))
            throw new CentException(400, input.Error == "access_denied" ? "google_cancelled" : "google_authorization_failed", "Google sign-in did not complete. Start again.");
        if (string.IsNullOrWhiteSpace(input.Code)) throw CentGoogleSettings.Invalid();
        var identity = await google.ExchangeAndValidate(settings, input.Code, input.CodeVerifier, attempt.NonceHash, ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.GoogleSubject == identity.Subject, ct);
        if (user is null) {
            var normalizedEmail = identity.Email.ToUpperInvariant();
            if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, ct)) throw Conflict();
            user = new CentUser { GoogleSubject = identity.Subject, Email = identity.Email, NormalizedEmail = normalizedEmail, Name = identity.Name };
            db.Users.Add(user);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) {
                // Unique subject and email indexes arbitrate concurrent registration, including password registration.
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

    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static CentException Conflict() => new(409, "google_account_conflict", "This email already belongs to a Cent account. Use its existing sign-in method. Accounts are not linked automatically.");
}

public static class CentGoogleEndpoints
{
    public static void MapCentGoogleAuth(this RouteGroupBuilder auth)
    {
        auth.MapPost("/google/start", async (CentGoogleAuth service, CancellationToken ct) => Results.Ok(await service.Start(ct)));
        auth.MapPost("/google/complete", async (CentGoogleComplete input, CentGoogleAuth service, CancellationToken ct) => Results.Ok(await service.Complete(input, ct)));
    }
}
