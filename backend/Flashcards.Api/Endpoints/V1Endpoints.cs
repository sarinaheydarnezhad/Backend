using System.ComponentModel.DataAnnotations;
using Flashcards.Application.Abstractions;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using System.Security.Claims;

namespace Flashcards.Api.Endpoints;

public static class V1Endpoints
{
    public static IEndpointRouteBuilder MapV1Endpoints(this IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapGroup("/api/v1").WithTags("v1");

        v1.MapGet("/status", (IServiceMetadata metadata) =>
            {
                var info = metadata.Get();
                return Results.Ok(new StatusResponse(info.Name, info.ApiVersion));
            })
            .WithName("GetV1Status");

        // The deck route establishes the boundary contract; implementation follows in a later feature.
        v1.MapPost("/decks", (CreateDeckRequest request) =>
            Results.Problem(statusCode: StatusCodes.Status501NotImplemented,
                title: "Deck creation is not available yet."))
            .WithName("CreateV1Deck").RequireAuthorization();

        var auth = v1.MapGroup("/auth").WithTags("auth").RequireRateLimiting("auth");
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
        auth.MapPost("/logout", async (ClaimsPrincipal principal, AuthService service, CancellationToken cancellationToken) =>
        {
            await service.LogoutAsync(principal, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization();

        var mine = v1.MapGroup("/me").WithTags("account").RequireAuthorization();
        mine.MapGet("/", async (ClaimsPrincipal principal, UserService users, CancellationToken cancellationToken) =>
        {
            var user = await users.FindByIdAsync(AuthenticatedIdentity.FromClaims(principal).UserId, cancellationToken);
            return user is null ? Results.NotFound() : Results.Ok(new { user.Id, user.DisplayName, user.Email });
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
            return state is null ? Results.NotFound() : Results.Ok(new { state.CardId, state.Box, state.DueDate, state.TotalReviews });
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

        return endpoints;
    }

}

public sealed record StatusResponse(string Name, string ApiVersion);

public sealed record CreateDeckRequest(
    [property: Required(AllowEmptyStrings = false), StringLength(120, MinimumLength = 1)] string Name);

public sealed record RegisterRequest(
    [property: Required, EmailAddress, StringLength(256)] string Email,
    [property: Required, StringLength(128, MinimumLength = 12)] string Password,
    [property: Required, StringLength(120, MinimumLength = 1)] string DisplayName);

public sealed record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password);

public sealed record RefreshRequest([property: Required] string RefreshToken);
