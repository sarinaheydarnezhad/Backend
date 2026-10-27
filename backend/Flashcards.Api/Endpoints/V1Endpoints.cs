using System.ComponentModel.DataAnnotations;
using Flashcards.Application.Abstractions;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Sync;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Api.Endpoints;

public static class V1Endpoints
{
    public static IEndpointRouteBuilder MapV1Endpoints(this IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapGroup("/api/v1").WithTags("v1");
        v1.MapBackups();
        v1.MapSubscriptions();
        v1.MapMyketPayments();
        v1.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.User.Identity?.IsAuthenticated == true)
                context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (FreeTierLimitException exception)
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { error = exception.Message, code = exception.Code, limits = exception.Limits, usage = exception.Usage }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (SyncException exception)
            {
                if (exception.Code is not null)
                    return Results.Json(new { error = exception.Message, code = exception.Code }, statusCode: exception.StatusCode);
                return exception.Conflict is null
                    ? Results.BadRequest(new { error = exception.Message })
                    : Results.Conflict(new { error = exception.Message, conflict = exception.Conflict });
            }
            catch (Exception exception) when (exception is DbException or DbUpdateException or TimeoutException)
            {
                var http = context.HttpContext;
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("StorageFailures")
                    .LogWarning("Account operation failed ({FailureType}); trace {TraceId}", exception.GetType().Name, http.TraceIdentifier);
                http.Response.Headers.RetryAfter = "5";
                http.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { error = "Storage is temporarily unavailable. Retain local data and retry the same operation.",
                    code = "storage_unavailable", traceId = http.TraceIdentifier }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        v1.MapGet("/status", (IServiceMetadata metadata) =>
            {
                var info = metadata.Get();
                return Results.Ok(new StatusResponse(info.Name, info.ApiVersion));
            })
            .WithName("GetV1Status");

        v1.MapPost("/decks", async (CreateDeckRequest request, ClaimsPrincipal principal, SyncService service,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            var deck = await service.CreateDeckAsync(AuthenticatedIdentity.FromClaims(principal).UserId,
                request.Name, cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Created("/api/v1/me/decks", new CreateDeckResponse(deck.Id, deck.Name, deck.SyncVersion));
        })
            .WithName("CreateV1Deck").RequireAuthorization();

        var auth = v1.MapGroup("/auth").WithTags("auth").RequireRateLimiting("auth");
        auth.MapPhoneAuth();
        auth.MapGoogleAuth();
        auth.MapPost("/register", async (RegisterRequest request, AuthService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            var tokens = await service.RegisterAsync(request.Email, request.Password, request.DisplayName, cancellationToken);
            if (tokens is null) return Results.Conflict(new { error = "Email is already registered." });
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(tokens);
        });
        auth.MapPost("/login", async (LoginRequest request, AuthService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            var tokens = await service.LoginAsync(request.Email, request.Password, cancellationToken);
            if (tokens is null) return Results.Unauthorized();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(tokens);
        });
        auth.MapPost("/refresh", async (RefreshRequest request, AuthService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            var tokens = await service.RefreshAsync(request.RefreshToken, cancellationToken);
            if (tokens is null) return Results.Unauthorized();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(tokens);
        });
        auth.MapPost("/logout", async (ClaimsPrincipal principal, AuthService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            await service.LogoutAsync(principal, cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            return Results.NoContent();
        }).RequireAuthorization();
        auth.MapPost("/logout/{sessionId:guid}", async (Guid sessionId, ClaimsPrincipal principal, AuthService service,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            await service.LogoutSessionAsync(principal, sessionId, cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            return Results.NoContent();
        }).RequireAuthorization();

        var mine = v1.MapGroup("/me").WithTags("account").RequireAuthorization();
        mine.MapGet("/", async (ClaimsPrincipal principal, UserService users, CancellationToken cancellationToken) =>
        {
            var user = await users.FindByIdAsync(AuthenticatedIdentity.FromClaims(principal).UserId, cancellationToken);
            return user is null ? Results.NotFound() : Results.Ok(new { user.Id, user.DisplayName, user.Email, user.PhoneNumber });
        });
        mine.MapPut("/profile", async (UpdateProfileRequest request, ClaimsPrincipal principal, UserService users, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 120)
                return Results.BadRequest(new { error = "Enter a name between 1 and 120 characters." });
            var user = await users.UpdateDisplayNameAsync(AuthenticatedIdentity.FromClaims(principal).UserId, request.DisplayName, cancellationToken);
            return user is null ? Results.NotFound() : Results.Ok(new { user.Id, user.DisplayName, user.Email, user.PhoneNumber });
        });
        mine.MapGet("/decks", async (ClaimsPrincipal principal, FlashcardsQueries queries, CancellationToken cancellationToken) =>
            Results.Ok((await queries.GetUserDecksAsync(AuthenticatedIdentity.FromClaims(principal).UserId, cancellationToken: cancellationToken))
                .Select(deck => new { deck.Id, deck.Name, deck.ArchivedAtUtc })));
        mine.MapGet("/decks/{deckId:guid}/cards", async (Guid deckId, ClaimsPrincipal principal, FlashcardsQueries queries, CancellationToken cancellationToken) =>
            Results.Ok((await queries.GetCardsByDeckAsync(AuthenticatedIdentity.FromClaims(principal).UserId, deckId, cancellationToken: cancellationToken))
                .Select(card => new { card.Id, card.DeckId, card.FrontText, card.Meaning, card.ArchivedAtUtc })));
        mine.MapGet("/cards/{cardId:guid}/review-state", async (Guid cardId, ClaimsPrincipal principal, FlashcardsQueries queries, CancellationToken cancellationToken) =>
        {
            var state = await queries.GetReviewStateAsync(AuthenticatedIdentity.FromClaims(principal).UserId, cardId, cancellationToken);
            return state is null ? Results.NotFound() : Results.Ok(new
            {
                state.CardId, state.Box, state.DueDate, state.TotalReviews, state.TotalSuccesses,
                state.ConsecutiveSuccesses, state.LastReviewedAtUtc
            });
        });
        mine.MapGet("/cards/{cardId:guid}/review-history", async (Guid cardId, ClaimsPrincipal principal, FlashcardsQueries queries, CancellationToken cancellationToken) =>
            Results.Ok((await queries.GetReviewHistoryAsync(AuthenticatedIdentity.FromClaims(principal).UserId, cardId, cancellationToken))
                .Select(review => new { review.Id, review.CardId, review.Result, review.PreviousBox, review.NewBox, review.ReviewedAtUtc })));
        mine.MapGet("/settings", async (ClaimsPrincipal principal, FlashcardsQueries queries, CancellationToken cancellationToken) =>
        {
            var settings = await queries.GetUserSettingsAsync(AuthenticatedIdentity.FromClaims(principal).UserId, cancellationToken);
            return settings is null ? Results.NotFound() : Results.Ok(new
            {
                settings.Theme, settings.HapticsEnabled, settings.Language, settings.DailyReminderEnabled,
                settings.DailyReminderTime, settings.PreferredSpeechLanguage, settings.PreferredSpeechAccent
            });
        });

        var sync = v1.MapGroup("/sync").WithTags("sync").RequireAuthorization();
        sync.MapPost("/push", (HttpContext context, ClaimsPrincipal principal, SyncService service, CancellationToken cancellationToken) =>
            PushAsync(context, principal, service, false, cancellationToken));
        sync.MapPost("/guest-migration", (HttpContext context, ClaimsPrincipal principal, SyncService service, CancellationToken cancellationToken) =>
            PushAsync(context, principal, service, true, cancellationToken));
        sync.MapGet("/pull", async (long cursor, int? limit, ClaimsPrincipal principal, SyncService service, HttpContext context, CancellationToken cancellationToken) =>
            Results.Ok(await service.PullAsync(AuthenticatedIdentity.FromClaims(principal).UserId, cursor, limit ?? 100, cancellationToken)));

        return endpoints;
    }

    private static async Task<IResult> PushAsync(HttpContext context, ClaimsPrincipal principal, SyncService service,
        bool guestMigration, CancellationToken cancellationToken)
    {
        var maxBytes = guestMigration ? 10_485_760 : 131_072;
        if (context.Request.ContentLength > maxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            buffer.Write(chunk, 0, read);
        }

        SyncPushRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<SyncPushRequest>(buffer.ToArray(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
        }
        catch (JsonException) { return Results.BadRequest(new { error = "Invalid sync JSON." }); }

        var userId = AuthenticatedIdentity.FromClaims(principal).UserId;
        var response = guestMigration
            ? await service.MigrateGuestAsync(userId, request!, cancellationToken)
            : await service.PushAsync(userId, request!, cancellationToken);
        if (guestMigration)
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GuestMigration")
                .LogInformation("Guest migration committed {ChangeCount} acknowledgements at cursor {Cursor}; trace {TraceId}",
                    response.Changes.Count, response.Cursor, context.TraceIdentifier);
        return Results.Ok(response);
    }

}

public sealed record StatusResponse(string Name, string ApiVersion);

public sealed record CreateDeckRequest(
    [property: Required(AllowEmptyStrings = false), StringLength(120, MinimumLength = 1)] string Name);

public sealed record CreateDeckResponse(Guid Id, string Name, long SyncVersion);

public sealed record UpdateProfileRequest(
    [property: Required, StringLength(120, MinimumLength = 1)] string DisplayName);

public sealed record RegisterRequest(
    [property: Required, EmailAddress, StringLength(256)] string Email,
    [property: Required, StringLength(128, MinimumLength = 12)] string Password,
    [property: Required, StringLength(120, MinimumLength = 1)] string DisplayName);

public sealed record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password);

public sealed record RefreshRequest([property: Required] string RefreshToken);
