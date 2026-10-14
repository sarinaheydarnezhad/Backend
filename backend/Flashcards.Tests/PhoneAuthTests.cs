using System.Net;
using System.Net.Http.Headers;
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

public sealed class PhoneAuthTests : IAsyncLifetime
{
    private const string Phone = "+989121234567";
    private const string Password = "Private passphrase 2026!";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestSmsSender _sms = new();
    private readonly TestClock _clock = new();
    private PhoneApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connection.CreateFunction("ISJSON", (string value) =>
        {
            try { using var document = JsonDocument.Parse(value); return 1; }
            catch (JsonException) { return 0; }
        });
        await _connection.OpenAsync();
        _factory = new PhoneApiFactory(_connection, _sms, _clock);
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Verified_registration_hashes_password_and_creates_existing_jwt_session()
    {
        var tokens = await RegisterAsync(Phone, "Student");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/");
        Assert.Equal(Phone, me.GetProperty("phoneNumber").GetString());
        Assert.Equal("Student", me.GetProperty("displayName").GetString());
        Assert.False(me.TryGetProperty("passwordHash", out _));
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var user = await database.Users.SingleAsync();
        Assert.NotEqual(Password, user.PasswordHash);
        Assert.Single(await database.AuthSessions.ToListAsync());
        Assert.Single(await database.SyncHeads.ToListAsync());
        Assert.NotEqual(tokens.RefreshToken, (await database.RefreshTokens.SingleAsync()).TokenHash);
        var challenge = await database.OtpChallenges.SingleAsync();
        Assert.NotNull(challenge.ConsumedAtUtc);
        Assert.NotEqual(_sms.Code, challenge.CodeHash);
        Assert.Equal(64, challenge.CodeHash.Length);
    }

    [Fact]
    public async Task Password_login_uses_phone_and_rejects_invalid_credentials()
    {
        await RegisterAsync(Phone, "Student");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/phone/login",
            new { phoneNumber = Phone, password = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/phone/login",
            new { phoneNumber = "+989121234568", password = Password })).StatusCode);
        var response = await _client.PostAsJsonAsync("/api/v1/auth/phone/login", new { phoneNumber = Phone, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/me/")).StatusCode);
    }

    [Fact]
    public async Task Otp_login_needs_no_password_and_logout_revokes_the_session()
    {
        await RegisterAsync(Phone, "Student");
        _clock.Advance(TimeSpan.FromSeconds(61));
        var challenge = await RequestAsync(Phone, "login");
        var response = await VerifyAsync(challenge.ChallengeId, _sms.Code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<OtpVerificationResponse>())!;
        Assert.Null(result.RegistrationToken);
        Assert.NotNull(result.Tokens);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = result.Tokens.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.ChallengeId, _sms.Code)).StatusCode);
    }

    [Fact]
    public async Task Phone_sessions_preserve_refresh_rotation_and_reuse_detection()
    {
        var original = await RegisterAsync(Phone, "Student");
        var response = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = original.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
        Assert.NotEqual(original.RefreshToken, rotated.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = original.RefreshToken })).StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/me/")).StatusCode);
    }

    [Fact]
    public async Task Registration_proof_is_required_bound_to_phone_and_single_use()
    {
        var proof = await RegistrationProofAsync(Phone);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CompleteAsync("+989121234568", proof)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CompleteAsync(Phone, "forged-proof")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/auth/phone/register", new
        {
            phoneNumber = Phone, registrationToken = proof, password = Password, confirmPassword = "different", username = "Student"
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(Phone, proof)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CompleteAsync(Phone, proof)).StatusCode);
    }

    [Fact]
    public async Task Duplicate_phone_is_rejected_but_usernames_need_not_be_unique()
    {
        await RegisterAsync(Phone, "Same name");
        await RegisterAsync("+989121234568", "Same name");
        _clock.Advance(TimeSpan.FromSeconds(61));
        var proof = await RegistrationProofAsync(Phone);
        var duplicate = await CompleteAsync(Phone, proof);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(2, await database.Users.CountAsync());
        Assert.Equal(2, await database.Users.CountAsync(item => item.DisplayName == "Same name"));
    }

    [Fact]
    public async Task Invalid_and_expired_codes_and_expired_registration_proofs_are_rejected()
    {
        var challenge = await RequestAsync(Phone, "register");
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.ChallengeId, "not-six")).StatusCode);
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.ChallengeId, _sms.Code)).StatusCode);
        var proof = await RegistrationProofAsync(Phone);
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CompleteAsync(Phone, proof)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Users.ToListAsync());
    }

    [Fact]
    public async Task Verification_attempts_are_persisted_and_limited_to_five()
    {
        var challenge = await RequestAsync(Phone, "register");
        var wrong = _sms.Code == "000000" ? "111111" : "000000";
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.ChallengeId, wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.ChallengeId, _sms.Code)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(5, (await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().OtpChallenges.SingleAsync()).Attempts);
    }

    [Fact]
    public async Task Requests_have_a_phone_cooldown_and_hourly_limit_and_resend_invalidates_old_code()
    {
        var first = await RequestAsync(Phone, "register");
        var firstCode = _sms.Code;
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request",
            new { phoneNumber = Phone, purpose = "register" })).StatusCode);
        for (var request = 1; request < 5; request++)
        {
            _clock.Advance(TimeSpan.FromSeconds(61));
            await RequestAsync(Phone, "register");
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(first.ChallengeId, firstCode)).StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request",
            new { phoneNumber = Phone, purpose = "login" })).StatusCode);
        Assert.Equal(5, _sms.Sends);
    }

    [Fact]
    public async Task Phone_endpoints_keep_the_existing_per_ip_rate_limit()
    {
        for (var attempt = 0; attempt < 10; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/phone/login",
                new { phoneNumber = Phone, password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _client.PostAsJsonAsync("/api/v1/auth/phone/login",
            new { phoneNumber = Phone, password = Password })).StatusCode);
    }

    [Theory]
    [InlineData("09121234567")]
    [InlineData("+0000000000")]
    [InlineData("+98abc1234567")]
    public async Task Invalid_phone_numbers_are_rejected_without_sms(string phoneNumber)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request",
            new { phoneNumber, purpose = "register" })).StatusCode);
        Assert.Equal(0, _sms.Sends);
    }

    [Fact]
    public async Task Persian_digits_are_normalized_to_the_same_phone_identity()
    {
        await RequestAsync("+۹۸۹۱۲۱۲۳۴۵۶۷", "register");
        Assert.Equal(Phone, _sms.PhoneNumber);
    }

    [Fact]
    public async Task Missing_or_failed_sms_delivery_never_produces_usable_verification()
    {
        _sms.Configured = false;
        var missing = await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = Phone, purpose = "register" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        _sms.Configured = true;
        _sms.Fail = true;
        var failed = await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = Phone, purpose = "register" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var challenge = await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().OtpChallenges.SingleAsync();
        Assert.False(challenge.Delivered);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.Id, _sms.Code)).StatusCode);
    }

    private async Task<OtpRequestResponse> RequestAsync(string phoneNumber, string purpose)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber, purpose });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return (await response.Content.ReadFromJsonAsync<OtpRequestResponse>())!;
    }

    private Task<HttpResponseMessage> VerifyAsync(Guid challengeId, string code) =>
        _client.PostAsJsonAsync("/api/v1/auth/phone/otp/verify", new { challengeId, code });

    private async Task<string> RegistrationProofAsync(string phoneNumber)
    {
        var challenge = await RequestAsync(phoneNumber, "register");
        var response = await VerifyAsync(challenge.ChallengeId, _sms.Code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var verified = (await response.Content.ReadFromJsonAsync<OtpVerificationResponse>())!;
        Assert.Null(verified.Tokens);
        Assert.NotNull(verified.RegistrationToken);
        return verified.RegistrationToken;
    }

    private Task<HttpResponseMessage> CompleteAsync(string phoneNumber, string registrationToken, string username = "Student") =>
        _client.PostAsJsonAsync("/api/v1/auth/phone/register", new
        {
            phoneNumber, registrationToken, password = Password, confirmPassword = Password, username
        });

    private async Task<AuthTokens> RegisterAsync(string phoneNumber, string username)
    {
        var proof = await RegistrationProofAsync(phoneNumber);
        var response = await CompleteAsync(phoneNumber, proof, username);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class TestSmsSender : ISmsOtpSender
    {
        public bool Configured { get; set; } = true;
        public bool Fail { get; set; }
        public bool IsConfigured => Configured;
        public int Sends { get; private set; }
        public string PhoneNumber { get; private set; } = "";
        public string Code { get; private set; } = "";
        public Task SendAsync(string phoneNumber, string code, CancellationToken cancellationToken)
        {
            PhoneNumber = phoneNumber;
            Code = code;
            Sends++;
            if (Fail) throw new PhoneAuthException(503, "sms_unavailable", "SMS unavailable.");
            return Task.CompletedTask;
        }
    }

    private sealed class PhoneApiFactory(SqliteConnection connection, ISmsOtpSender sender, TimeProvider clock) : TestApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<FlashcardsDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<FlashcardsDbContext>>();
                services.AddDbContext<FlashcardsDbContext>(options => options.UseSqlite(connection));
                services.RemoveAll<ISmsOtpSender>();
                services.AddSingleton(sender);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            });
        }
    }
}
