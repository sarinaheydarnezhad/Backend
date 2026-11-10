using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed partial class PhoneAuthTests
{
    [Theory]
    [InlineData("+14155552671")]
    [InlineData("09121234567")]
    [InlineData("+982112345678")]
    public async Task Legacy_foreign_local_or_landline_bindings_cannot_open_signup_for_duplicate_accounts(string oldPhone)
    {
        var original = await SeedLegacyAsync(verified: true);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Users.Where(x => x.Id == original.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.PhoneNumber, oldPhone).SetProperty(x => x.VerifiedPhoneNumber, oldPhone));
        var challenge = await RequestAsync("+989121234568");
        Assert.Equal(HttpStatusCode.Conflict, (await VerifyAsync(challenge.ChallengeId, _sms.Code)).StatusCode);
        using var inspect = _factory.Services.CreateScope();
        var db = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var saved = await db.Users.SingleAsync();
        Assert.Equal(original.Id, saved.Id); Assert.Equal(oldPhone, saved.PhoneNumber);
        Assert.Empty(await db.AuthSessions.ToListAsync()); Assert.Empty(await db.RefreshTokens.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unverified_legacy_accounts_require_recovery_without_creating_or_modifying_accounts(bool emailOnly)
    {
        var original = await SeedLegacyAsync(emailOnly: emailOnly);
        var challenge = await RequestAsync(Phone);
        var response = await VerifyAsync(challenge.ChallengeId, _sms.Code);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("account_recovery_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.ChallengeId, _sms.Code)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var saved = await database.Users.SingleAsync();
        Assert.Equal(original.Id, saved.Id); Assert.Equal(original.PhoneNumber, saved.PhoneNumber);
        Assert.Equal(original.PasswordHash, saved.PasswordHash); Assert.Equal(original.Email, saved.Email);
        Assert.Null(saved.PhoneVerifiedAtUtc); Assert.Empty(await database.AuthSessions.ToListAsync()); Assert.Empty(await database.RefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task Unknown_phone_cannot_duplicate_a_legacy_account_with_an_unresolved_phone()
    {
        await SeedLegacyAsync();
        var challenge = await RequestAsync("+989121234568");
        Assert.Equal(HttpStatusCode.Conflict, (await VerifyAsync(challenge.ChallengeId, _sms.Code)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Users.ToListAsync());
    }

    [Fact]
    public async Task Archived_account_phone_is_reserved_without_creating_a_replacement()
    {
        var user = await SeedLegacyAsync(verified: true);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Users.Where(x => x.Id == user.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ArchivedAtUtc, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.Conflict, (await VerifyAsync((await RequestAsync(Phone)).ChallengeId, _sms.Code)).StatusCode);
    }

    [Fact]
    public async Task Legacy_refresh_and_access_sessions_are_rejected_even_for_verified_users()
    {
        var user = await SeedLegacyAsync(verified: true);
        var tokens = await AuthenticateAsync(await RequestAsync(Phone));
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().AuthSessions.Where(x => x.Id == tokens.SessionId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.OtpAuthenticated, false));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { tokens.RefreshToken })).StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await AuthenticateAsync(await RequestAsync(Phone));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/me/")).StatusCode);
    }

    [Fact]
    public async Task Revoked_phone_verification_blocks_refresh_and_access_despite_signed_client_claims()
    {
        var tokens = await AuthenticateAsync(await RequestAsync(Phone));
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Users.ExecuteUpdateAsync(u => u.SetProperty(x => x.PhoneVerifiedAtUtc, (DateTimeOffset?)null));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { tokens.RefreshToken })).StatusCode);
    }

    private async Task<User> SeedLegacyAsync(bool verified = false, bool emailOnly = false)
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var user = new User { DisplayName = "Existing learner", PhoneNumber = emailOnly ? null : Phone,
            Email = emailOnly ? "legacy@example.com" : null, NormalizedEmail = emailOnly ? "LEGACY@EXAMPLE.COM" : null,
            VerifiedPhoneNumber = verified ? Phone : null, PhoneVerifiedAtUtc = verified ? DateTimeOffset.UtcNow : null };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, Password);
        database.Users.Add(user); database.SyncHeads.Add(new SyncHead { UserId = user.Id });
        await database.SaveChangesAsync(); return user;
    }
}
