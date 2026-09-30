using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Flashcards.Infrastructure.Auth;

public readonly record struct AuthenticatedIdentity(Guid UserId, Guid SessionId)
{
    public static AuthenticatedIdentity FromClaims(ClaimsPrincipal principal) => new(
        Guid.Parse(principal.FindFirst(JwtRegisteredClaimNames.Sub)!.Value),
        Guid.Parse(principal.FindFirst("sid")!.Value));
}
