using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace Cent.Module;

public sealed class CentGoogleIdTokenValidator(CentGooglePublicKeys keys) : ICentGoogleIdTokenValidator
{
    public async Task<CentGoogleIdentity> Validate(string token, string clientId, string nonceHash, CancellationToken ct)
    {
        try {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 10000) throw CentGoogleSettings.Invalid();
            var parts = token.Split('.');
            if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) throw CentGoogleSettings.Invalid();
            using var header = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[0]));
            RequireUniqueProperties(header.RootElement);
            if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != SecurityAlgorithms.RsaSha256 ||
                !header.RootElement.TryGetProperty("kid", out var kid) || kid.ValueKind != JsonValueKind.String ||
                kid.GetString() is not { Length: > 0 and <= 256 } keyId || header.RootElement.TryGetProperty("crit", out _))
                throw CentGoogleSettings.Invalid();
            // The untrusted header only selects a key from a FIXED Google endpoint. Never follow jku/x5u.
            var key = await keys.Get(keyId, ct);
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 10000 };
            handler.ValidateToken(token, new TokenValidationParameters {
                RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                TryAllIssuerSigningKeys = false, ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateIssuer = true, ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
                ValidateAudience = true, RequireAudience = true, ValidAudience = clientId,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.Zero,
                IncludeTokenOnFailedValidation = false
            }, out _);
            // Interpret application claims ONLY after cryptographic validation by IdentityModel.
            using var claims = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1]));
            RequireUniqueProperties(claims.RootElement);
            CentGoogleClient.ValidateAdditionalClaims(claims.RootElement, clientId, nonceHash);
            var root = claims.RootElement;
            var subject = root.GetProperty("sub").GetString();
            var email = root.GetProperty("email").GetString();
            if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255 || subject.Any(c => c < 33 || c > 126) ||
                root.GetProperty("email_verified").ValueKind != JsonValueKind.True ||
                string.IsNullOrWhiteSpace(email) || email.Length > 256 ||
                !MailAddress.TryCreate(email, out var address) || address.Address != email)
                throw CentGoogleSettings.Invalid();
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name)) name = email.Split('@')[0];
            return new(subject, email, name.Length > 120 ? name[..120] : name);
        }
        catch (Exception e) when (e is SecurityTokenException or JsonException or FormatException or ArgumentException or
            InvalidOperationException or KeyNotFoundException) { throw CentGoogleSettings.Invalid(); }
    }

    internal static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new JsonException();
    }
}

// Instance-local Cent cache: no shared Google library settings, static caches or middleware are changed.
public sealed class CentGooglePublicKeys(IHttpClientFactory clients, TimeProvider? time = null) : IDisposable
{
    public const string Endpoint = "https://www.googleapis.com/oauth2/v1/certs";
    public const string HttpClientName = "Cent.Google.GisCertificates";
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyDictionary<string, SecurityKey> cached = new Dictionary<string, SecurityKey>();
    private DateTimeOffset expiresAt;
    private DateTimeOffset nextUnknownRefresh;
    private DateTimeOffset retryAfter;

    public async Task<SecurityKey> Get(string kid, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            var now = clock.GetUtcNow();
            if (now < expiresAt && cached.TryGetValue(kid, out var existing)) return existing;
            if (now < retryAfter) throw CentGoogleSettings.Unavailable();
            if (now < expiresAt && now < nextUnknownRefresh) throw CentGoogleSettings.Invalid();
            try {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var fetchCt = deadline.Token;
                using var http = clients.CreateClient(HttpClientName);
                using var response = await http.GetAsync(Endpoint, HttpCompletionOption.ResponseHeadersRead, fetchCt);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(fetchCt);
                var bytes = await ReadBounded(stream, 65536, fetchCt);
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
                CentGoogleIdTokenValidator.RequireUniqueProperties(document.RootElement);
                var refreshed = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
                var certExpiry = DateTimeOffset.MaxValue;
                now = clock.GetUtcNow();
                foreach (var property in document.RootElement.EnumerateObject()) {
                    if (refreshed.Count >= 16 || property.Name.Length is 0 or > 256 ||
                        property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { Length: <= 16384 } pem)
                        throw new JsonException();
                    using var cert = X509Certificate2.CreateFromPem(pem);
                    if (cert.NotBefore.ToUniversalTime() > now.UtcDateTime || cert.NotAfter.ToUniversalTime() <= now.UtcDateTime)
                        throw new CryptographicException();
                    using var rsa = cert.GetRSAPublicKey();
                    if (rsa is null || rsa.KeySize < 2048) throw new CryptographicException();
                    refreshed.Add(property.Name, new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = property.Name });
                    if (cert.NotAfter.ToUniversalTime() < certExpiry) certExpiry = cert.NotAfter.ToUniversalTime();
                }
                if (refreshed.Count == 0) throw new JsonException();
                var cacheControl = response.Headers.CacheControl;
                var maxAge = cacheControl?.MaxAge ?? TimeSpan.FromMinutes(1);
                var age = response.Headers.Age ?? TimeSpan.Zero;
                if (response.Headers.Date is { } date && now > date) age += now - date;
                var seconds = cacheControl?.NoStore == true || cacheControl?.NoCache == true ? 0
                    : Math.Clamp((maxAge - age).TotalSeconds, 0, TimeSpan.FromHours(6).TotalSeconds);
                cached = refreshed;
                expiresAt = now.AddSeconds(seconds) < certExpiry ? now.AddSeconds(seconds) : certExpiry;
                nextUnknownRefresh = now.AddSeconds(30);
                retryAfter = default;
                if (!cached.TryGetValue(kid, out var key)) throw CentGoogleSettings.Invalid();
                return key;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or CryptographicException or
                ArgumentException or InvalidOperationException or CentBodyTooLargeException || e is OperationCanceledException && !ct.IsCancellationRequested) {
                retryAfter = clock.GetUtcNow().AddSeconds(5);
                throw CentGoogleSettings.Unavailable();
            }
        }
        finally { gate.Release(); }
    }

    internal static async Task<byte[]> ReadBounded(Stream stream, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(4096, limit + 1)];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) != 0) {
            if (buffer.Length + read > limit) throw new CentBodyTooLargeException();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public void Dispose() => gate.Dispose();
}

internal sealed class CentBodyTooLargeException : Exception;
