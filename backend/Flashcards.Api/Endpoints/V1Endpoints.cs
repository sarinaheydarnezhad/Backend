using System.ComponentModel.DataAnnotations;
using Flashcards.Application.Abstractions;

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
            .WithName("CreateV1Deck");

        return endpoints;
    }
}

public sealed record StatusResponse(string Name, string ApiVersion);

public sealed record CreateDeckRequest(
    [property: Required(AllowEmptyStrings = false), StringLength(120, MinimumLength = 1)] string Name);
