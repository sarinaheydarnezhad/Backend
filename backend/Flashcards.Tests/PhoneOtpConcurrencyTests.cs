using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flashcards.Tests;

public sealed class PhoneOtpConcurrencyTests
{
    [Fact]
    public async Task Concurrent_registration_consumes_sms_once_and_creates_one_account_and_session()
    {
        var connectionString = $"Data Source=otp-register-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var sms = new CapturingSender();
        await using var factory = new Factory(connectionString, sms);
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Database.EnsureCreatedAsync();
        using var request = await client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = "09121234567", purpose = "register" });
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        var challenge = (await request.Content.ReadFromJsonAsync<OtpRequestResponse>())!;
        using var verification = await client.PostAsJsonAsync("/api/v1/auth/phone/otp/verify", new { challengeId = challenge.ChallengeId, code = sms.Code });
        Assert.Equal(HttpStatusCode.OK, verification.StatusCode);
        var proof = (await verification.Content.ReadFromJsonAsync<OtpVerificationResponse>())!;
        using var start = new SemaphoreSlim(0);
        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await start.WaitAsync();
            return await client.PostAsJsonAsync("/api/v1/auth/phone/register", new {
                phoneNumber = "09121234567", registrationToken = proof.RegistrationToken,
                password = "Concurrent password2026!", confirmPassword = "Concurrent password2026!", username = "Student" });
        })).ToArray();
        start.Release(2);
        var responses = await Task.WhenAll(attempts);
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
            using var scope = factory.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var user = await database.Users.SingleAsync();
            Assert.Equal(user.PhoneNumber, user.VerifiedPhoneNumber);
            Assert.NotNull(user.PhoneVerifiedAtUtc); Assert.NotNull(user.PasswordHash);
            Assert.Single(await database.AuthSessions.ToListAsync());
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Concurrent_verifications_issue_one_proof_and_no_session()
    {
        var connectionString = $"Data Source=otp-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var sms = new CapturingSender();
        await using var factory = new Factory(connectionString, sms);
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Database.EnsureCreatedAsync();
        var request = await client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = "09121234567", purpose = "register" });
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        var challenge = (await request.Content.ReadFromJsonAsync<OtpRequestResponse>())!;
        using var start = new SemaphoreSlim(0);
        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await start.WaitAsync();
            return await client.PostAsJsonAsync("/api/v1/auth/phone/otp/verify", new { challengeId = challenge.ChallengeId, code = sms.Code });
        })).ToArray();
        start.Release(2);
        var responses = await Task.WhenAll(attempts);
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Empty(await db.Users.ToListAsync()); Assert.Empty(await db.AuthSessions.ToListAsync());
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    // This adapter exists only in the test assembly; runtime configuration cannot select it.
    private sealed class CapturingSender : ISmsOtpSender
    {
        public bool IsConfigured => true;
        public string Code { get; private set; } = "";
        public Task SendAsync(string phoneNumber, string code, CancellationToken cancellationToken)
        {
            Code = code;
            return Task.CompletedTask;
        }
    }

    private sealed class Factory(string connectionString, ISmsOtpSender sms) : TestApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<FlashcardsDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<FlashcardsDbContext>>();
                services.AddDbContext<FlashcardsDbContext>(options =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.CreateFunction("ISJSON", (string value) =>
                    {
                        try { using var json = JsonDocument.Parse(value); return 1; }
                        catch (JsonException) { return 0; }
                    });
                    options.UseSqlite(connection, contextOwnsConnection: true);
                });
                services.RemoveAll<ISmsOtpSender>();
                services.AddSingleton(sms);
            });
        }
    }
}
