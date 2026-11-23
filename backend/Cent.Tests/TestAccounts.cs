using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Cent.Module;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Cent.Tests;

// Commerce regressions seed an authenticated fixture, rather than exposing a password login endpoint.
internal static class TestAccounts
{
    internal static async Task<HttpResponseMessage> Create(WebApplicationFactory<Program> factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentDbContext>();
        var user = new CentUser { Email = email, NormalizedEmail = email.ToUpperInvariant(), Name = "Student" };
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);
        db.AddRange(user, new CentSession { UserId = user.Id, TokenHash = CentModule.Hash(token), ExpiresAt = expiresAt });
        await db.SaveChangesAsync();
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { token, expiresAt, user = new { user.Id, user.Name, user.Email, user.Role } }) };
    }
}
