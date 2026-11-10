using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

// Capture transport is test-assembly-only, never selectable by production configuration.
public sealed class TestSmsOutbox : ISmsOtpSender
{
    internal static readonly ConcurrentDictionary<string, string> Codes = new();
    public bool IsConfigured => true;
    public Task SendAsync(string phoneNumber, string code, CancellationToken cancellationToken)
    { Codes[phoneNumber] = code; return Task.CompletedTask; }
}

internal static class VerifiedRegistration
{
    private static int phoneSequence;
    public static async Task<HttpResponseMessage> RegisterOtpFixtureAsync(this HttpClient client, string email, string unusedLegacyPassword,
        string displayName = "Student", IServiceProvider? services = null)
    {
        var phone = "+989" + Interlocked.Increment(ref phoneSequence).ToString("D9");
        Assert.NotNull(services);
        using var scope = services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<PhoneAuthService>();
        var challenge = await service.RequestAsync(phone);
        Assert.True(TestSmsOutbox.Codes.TryRemove(phone, out var code));
        var verified = await service.VerifyAsync(challenge.ChallengeId, code!);
        Assert.NotNull(verified.Tokens);
        // Legacy profile metadata is seeded only by test code, after real OTP authentication.
        var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        await db.Users.Where(x => x.PhoneNumber == phone).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.Email, email).SetProperty(x => x.NormalizedEmail, email.ToUpperInvariant()).SetProperty(x => x.DisplayName, displayName));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(verified.Tokens) };
    }
}
