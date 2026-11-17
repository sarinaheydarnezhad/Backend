using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cent.Module;

public static class CentGoogleMode
{
    public const string AuthorizationCode = "AuthorizationCode";
    public const string GisIdToken = "GisIdToken";

    public static string Read(IConfiguration config) => config["Cent:Google:Mode"] switch {
        null or "" or AuthorizationCode => AuthorizationCode,
        GisIdToken => GisIdToken,
        _ => throw CentGoogleSettings.Unavailable()
    };

    public static string GisClientId(IConfiguration config)
    {
        var id = config["Cent:Google:ClientId"]?.Trim();
        if (!config.GetValue<bool>("Cent:Google:Enabled") || Read(config) != GisIdToken ||
            string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsWhiteSpace) || id.Any(char.IsControl))
            throw CentGoogleSettings.Unavailable();
        return id;
    }
}

public sealed record CentGoogleGisChallenge(string ChallengeId, string Nonce, string BrowserBinding, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "Cent GIS challenge (browser proof redacted)";
}
public sealed record CentGoogleGisComplete(string ChallengeId, string BrowserBinding, string IdToken)
{
    public override string ToString() => "Cent GIS completion (credentials redacted)";
}

public interface ICentGoogleIdTokenValidator
{
    Task<CentGoogleIdentity> Validate(string token, string clientId, string nonceHash, CancellationToken ct);
}

public sealed class CentGoogleGisAuth(CentDbContext db, IConfiguration config, CentGoogleAuth accounts,
    ICentGoogleIdTokenValidator validator)
{
    public async Task<CentGoogleGisChallenge> Challenge(string origin, CancellationToken ct)
    {
        _ = CentGoogleMode.GisClientId(config);
        var id = Random(); var nonce = Random(); var binding = Random();
        var now = DateTimeOffset.UtcNow; var expires = now.AddMinutes(5);
        await db.GoogleAttempts.Where(a => a.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        // Domain separation prevents either flow from consuming the other's attempts. No schema change.
        db.GoogleAttempts.Add(new CentGoogleAttempt {
            StateHash = ChallengeHash(id), VerifierHash = BindingHash(origin, binding),
            NonceHash = CentModule.Hash(nonce), ExpiresAt = expires
        });
        await db.SaveChangesAsync(ct);
        return new(id, nonce, binding, expires);
    }

    public async Task<object> Complete(CentGoogleGisComplete input, string origin, CancellationToken ct)
    {
        var clientId = CentGoogleMode.GisClientId(config);
        if (!IsRandom(input.ChallengeId) || !IsRandom(input.BrowserBinding) ||
            string.IsNullOrWhiteSpace(input.IdToken) || input.IdToken.Length > 10000)
            throw new CentException(400, "invalid_request", "Invalid GIS completion format.");
        var stateHash = ChallengeHash(input.ChallengeId); var bindingHash = BindingHash(origin, input.BrowserBinding);
        var now = DateTimeOffset.UtcNow;
        var pending = db.GoogleAttempts.Where(a => a.StateHash == stateHash && a.VerifierHash == bindingHash && a.ExpiresAt > now);
        var attempt = await pending.AsNoTracking().SingleOrDefaultAsync(ct);
        var consumedAt = DateTimeOffset.UtcNow;
        if (attempt is null || await pending.Where(a => a.ExpiresAt > consumedAt).ExecuteDeleteAsync(ct) != 1)
            throw CentGoogleSettings.Invalid();
        // Atomic consume BEFORE validation: concurrent requests and retries can never issue a second session.
        var identity = await validator.Validate(input.IdToken, clientId, attempt.NonceHash, ct);
        return await accounts.AuthenticateIdentity(identity, ct);
    }

    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static bool IsRandom(string? value) => value is { Length: 43 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_') &&
        WebEncoders.Base64UrlEncode(WebEncoders.Base64UrlDecode(value)) == value;
    private static string ChallengeHash(string id) => CentModule.Hash("cent-gis-challenge:" + id);
    private static string BindingHash(string origin, string binding) => CentModule.Hash("cent-gis-browser:" + origin + ":" + binding);
}

public static class CentGoogleGisEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4
    };

    public static void MapCentGoogleGis(this RouteGroupBuilder auth)
    {
        auth.MapGet("/google/config", (IConfiguration config, IHostEnvironment env) => {
            var mode = CentGoogleMode.Read(config);
            var enabled = config.GetValue<bool>("Cent:Google:Enabled");
            string? clientId = null;
            if (enabled) clientId = mode == CentGoogleMode.GisIdToken ? CentGoogleMode.GisClientId(config)
                : CentGoogleSettings.Read(config, env).ClientId;
            return Results.Ok(new { enabled, mode, clientId });
        });
        var gis = auth.MapGroup("/google/gis");
        gis.AddEndpointFilter(async (context, next) => {
            var http = context.HttpContext;
            var config = http.RequestServices.GetRequiredService<IConfiguration>();
            var env = http.RequestServices.GetRequiredService<IHostEnvironment>();
            var origin = http.Request.Headers.Origin.ToString();
            if (!(config.GetSection("Cent:AllowedOrigins").Get<string[]>() ?? []).Contains(origin, StringComparer.Ordinal) ||
                !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                !(uri.Scheme == "https" || env.IsDevelopment() && uri.Scheme == "http" && uri.IsLoopback) ||
                http.Request.Headers["X-Cent-GIS"].ToString() != "1")
                throw new CentException(403, "google_origin_rejected", "GIS requires an allowed Origin and X-Cent-GIS: 1.");
            // A non-simple JSON request plus explicit Origin enforcement prevents login CSRF.
            if (!string.Equals(http.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                throw new CentException(415, "unsupported_media_type", "Use application/json.");
            return await next(context);
        });
        gis.MapPost("/challenge", async (HttpContext http, CentGoogleGisAuth service, CancellationToken ct) => {
            var body = await ReadJson<JsonElement>(http.Request, 256, ct);
            if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any())
                throw new CentException(400, "invalid_request", "Challenge body must be an empty JSON object.");
            return Results.Ok(await service.Challenge(http.Request.Headers.Origin.ToString(), ct));
        });
        gis.MapPost("/complete", async (HttpContext http, CentGoogleGisAuth service, CancellationToken ct) => {
            var input = await ReadJson<CentGoogleGisComplete>(http.Request, 16384, ct);
            return Results.Ok(await service.Complete(input, http.Request.Headers.Origin.ToString(), ct));
        });
    }

    private static async Task<T> ReadJson<T>(HttpRequest request, int limit, CancellationToken ct)
    {
        if (request.ContentLength > limit) throw new CentException(413, "request_too_large", "GIS request is too large.");
        try {
            var bytes = await CentGooglePublicKeys.ReadBounded(request.Body, limit, ct);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            CentGoogleIdTokenValidator.RequireUniqueProperties(json.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new JsonException();
        }
        catch (CentBodyTooLargeException) { throw new CentException(413, "request_too_large", "GIS request is too large."); }
        catch (JsonException) { throw new CentException(400, "invalid_request", "Invalid GIS JSON request."); }
    }
}
