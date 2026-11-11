using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    [InlineData("+989121234567")] [InlineData("09121234567")]
    public async Task Existing_password_users_keep_original_ids_content_and_sessions_without_new_sms(string storedPhone)
    {
        var original = await SeedLegacy(storedPhone);
        _sms.Configured = false;
        var response = await Login(); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
        Assert.False(tokens.PhoneVerified); Assert.Equal(0, _sms.Sends);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { tokens.RefreshToken })).StatusCode);
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var saved = await db.Users.SingleAsync(); Assert.Equal(original.Id, saved.Id); Assert.Equal(original.PasswordHash, saved.PasswordHash);
        Assert.Equal("Preserved learning", (await db.Decks.SingleAsync()).Name);
    }

    [Fact]
    public async Task Unrelated_unverified_legacy_users_do_not_block_new_verified_signup()
    {
        var legacy = await SeedLegacy(null);
        var proof = await VerifyProof(await RequestAsync(Phone));
        await Register(proof.RegistrationToken!);
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Equal(legacy.PasswordHash, (await db.Users.SingleAsync(x => x.Id == legacy.Id)).PasswordHash);
    }

    [Fact]
    public async Task Password_reset_requires_otp_proof_preserves_account_and_revokes_all_old_sessions()
    {
        var original = await SeedLegacy();
        var login = await Login(); var old = (await login.Content.ReadFromJsonAsync<AuthTokens>())!;
        var proof = await VerifyProof(await RequestAsync(Phone, "reset"));
        Assert.Null(proof.Tokens); Assert.Null(proof.RegistrationToken); Assert.NotNull(proof.ResetPasswordToken);
        var reset = await Reset(proof.ResetPasswordToken!); Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var current = (await reset.Content.ReadFromJsonAsync<AuthTokens>())!; Assert.True(current.PhoneVerified);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", old.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { old.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(password: "New private password2026!")).StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(original.Id, (await db.Users.SingleAsync()).Id); Assert.Equal("Existing learner", (await db.Users.SingleAsync()).DisplayName);
        Assert.Equal("Preserved learning", (await db.Decks.SingleAsync()).Name);
    }

    [Fact]
    public async Task Recovery_proof_is_bound_to_phone_and_operation_and_can_only_be_used_once()
    {
        await SeedLegacy();
        var proof = await VerifyProof(await RequestAsync(Phone, "reset"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterResponse(proof.ResetPasswordToken!)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Reset(proof.ResetPasswordToken!, phone: "+989121234568")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Reset(proof.ResetPasswordToken!)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Reset(proof.ResetPasswordToken!)).StatusCode);
    }

    [Fact]
    public async Task Reset_failure_rolls_back_password_revocation_and_consumed_proof()
    {
        await SeedLegacy();
        var login = await Login(); var old = (await login.Content.ReadFromJsonAsync<AuthTokens>())!;
        var proof = await VerifyProof(await RequestAsync(Phone, "reset"));
        _failure.Enabled = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Reset(proof.ResetPasswordToken!)).StatusCode);
        _failure.Enabled = false;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", old.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Reset(proof.ResetPasswordToken!)).StatusCode);
    }

    [Fact]
    public async Task Reset_proof_expires_without_changing_existing_password()
    {
        await SeedLegacy();
        var proof = await VerifyProof(await RequestAsync(Phone, "reset"));
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Reset(proof.ResetPasswordToken!)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login()).StatusCode);
    }

    [Fact]
    public async Task Changing_reset_user_binding_invalidates_the_code_hash()
    {
        await SeedLegacy();
        var challenge = await RequestAsync(Phone, "reset");
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().OtpChallenges
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ActivationUserId, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.ChallengeId, _sms.Code)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login()).StatusCode);
    }

    [Fact]
    public async Task Unknown_wrong_password_and_archived_accounts_cannot_create_sessions()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = Phone, purpose = "reset" })).StatusCode);
        await SeedLegacy();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(password: "Wrong private password2026!")).StatusCode);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Users.ExecuteUpdateAsync(u => u.SetProperty(x => x.ArchivedAtUtc, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = Phone, purpose = "reset" })).StatusCode);
        Assert.Equal(0, _sms.Sends);
    }

    [Fact]
    public async Task Signup_code_or_proof_cannot_reset_an_existing_password()
    {
        await SeedLegacy("+989121234568");
        var proof = await VerifyProof(await RequestAsync(Phone));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Reset(proof.RegistrationToken!, phone: "+989121234568")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login("+989121234568")).StatusCode);
    }

    private async Task<User> SeedLegacy(string? phone = Phone)
    {
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var user = new User { PhoneNumber = phone, DisplayName = "Existing learner" };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, Password);
        db.Users.Add(user); db.SyncHeads.Add(new SyncHead { UserId = user.Id });
        db.Decks.Add(new Deck { UserId = user.Id, Name = "Preserved learning" });
        await db.SaveChangesAsync(); return user;
    }
}
