using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed partial class AuthTests
{
    [Fact]
    public async Task Refresh_storage_failure_rolls_back_consumption_and_the_original_token_can_retry()
    {
        var original = await RegisterAsync("refresh-recovery@example.com");
        _failure.Fail = true;
        var response = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = original.RefreshToken });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("storage_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.DoesNotContain(original.RefreshToken, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("private refresh details", await response.Content.ReadAsStringAsync());
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Null((await database.RefreshTokens.SingleAsync()).ConsumedAtUtc);
            Assert.Null((await database.AuthSessions.SingleAsync()).RevokedAtUtc);
        }
        _failure.Fail = false;
        var retry = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = original.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var rotated = (await retry.Content.ReadFromJsonAsync<AuthTokens>())!;
        Assert.NotEqual(original.RefreshToken, rotated.RefreshToken);
        Assert.Equal(original.SessionId, rotated.SessionId);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = original.RefreshToken })).StatusCode);
    }

    private sealed class RefreshFailureInterceptor : SaveChangesInterceptor
    {
        public bool Fail { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fail && eventData.Context!.ChangeTracker.Entries<RefreshToken>().Any(entry => entry.State == EntityState.Added))
                throw new DbUpdateException("private refresh details");
            return ValueTask.FromResult(result);
        }
    }
}
