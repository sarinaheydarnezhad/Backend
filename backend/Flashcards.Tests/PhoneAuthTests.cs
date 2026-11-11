using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flashcards.Tests;

public sealed partial class PhoneAuthTests : IAsyncLifetime
{
    private const string Phone = "+989121234567";
    private const string Password = "Private passphrase 2026!";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestSmsSender _sms = new();
    private readonly TestClock _clock = new();
    private readonly SessionFailure _failure = new();
    private PhoneApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connection.CreateFunction("ISJSON", (string value) => {
            try { using var document = JsonDocument.Parse(value); return 1; } catch (JsonException) { return 0; }
        });
        await _connection.OpenAsync();
        _factory = new PhoneApiFactory(_connection, _sms, _clock, _failure);
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Database.EnsureCreatedAsync();
    }
    public async Task DisposeAsync() { _client.Dispose(); await _factory.DisposeAsync(); await _connection.DisposeAsync(); }

    [Fact]
    public async Task Otp_verification_returns_signup_proof_without_creating_account_password_or_session()
    {
        var challenge = await RequestAsync(Phone);
        using (var scope = _factory.Services.CreateScope())
            Assert.NotEqual(_sms.Code, (await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().OtpChallenges.SingleAsync()).CodeHash);
        var proof = await VerifyProof(challenge);
        Assert.NotNull(proof.RegistrationToken); Assert.Null(proof.ResetPasswordToken); Assert.Null(proof.Tokens);
        using var inspect = _factory.Services.CreateScope(); var db = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Empty(await db.Users.ToListAsync()); Assert.Empty(await db.AuthSessions.ToListAsync()); Assert.Empty(await db.RefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task Verified_signup_hashes_password_saves_name_and_password_login_needs_no_sms()
    {
        var proof = await VerifyProof(await RequestAsync(Phone));
        var tokens = await Register(proof.RegistrationToken!);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        _sms.Configured = false;
        Assert.Equal(HttpStatusCode.OK, (await Login(Phone, Password)).StatusCode);
        Assert.Equal(1, _sms.Sends);
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var user = await db.Users.SingleAsync();
        Assert.True(PhoneVerification.IsVerified(user)); Assert.Equal("Student", user.DisplayName);
        Assert.NotEqual(Password, user.PasswordHash); Assert.NotNull(user.PasswordHash); Assert.Null(user.GoogleSubject);
        Assert.Single(await db.SyncHeads.ToListAsync()); Assert.Equal(2, await db.AuthSessions.CountAsync());
    }

    [Fact]
    public async Task Signup_requires_server_issued_phone_bound_proof_and_cannot_be_bypassed_by_flags()
    {
        var legacy = await _client.PostAsJsonAsync("/api/v1/auth/phone/register-password", new {
            phoneNumber = Phone, password = Password, confirmPassword = Password, phoneVerified = true });
        Assert.Equal(HttpStatusCode.Forbidden, legacy.StatusCode);
        var invalid = await RegisterResponse("client-supplied", Phone);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        var proof = await VerifyProof(await RequestAsync(Phone));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterResponse(proof.RegistrationToken!, "+989121234568")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RegisterResponse(proof.RegistrationToken!)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterResponse(proof.RegistrationToken!)).StatusCode);
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Single(await db.Users.ToListAsync()); Assert.Single(await db.AuthSessions.ToListAsync());
    }

    [Theory]
    [InlineData("password123", "password123", "invalid_password")]
    [InlineData("short1", "short1", "invalid_password")]
    [InlineData("OnlyLettersLong", "OnlyLettersLong", "invalid_password")]
    [InlineData("123456789012", "123456789012", "invalid_password")]
    [InlineData(Password, "Different password 2026!", "password_mismatch")]
    public async Task Invalid_password_does_not_consume_signup_proof(string password, string confirmation, string expectedCode)
    {
        var proof = await VerifyProof(await RequestAsync(Phone));
        var response = await _client.PostAsJsonAsync("/api/v1/auth/phone/register", new {
            phoneNumber = Phone, registrationToken = proof.RegistrationToken, password, confirmPassword = confirmation, username = "Student" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await RegisterResponse(proof.RegistrationToken!)).StatusCode);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Registration_proof_and_all_writes_roll_back_when_storage_fails(bool userSaveFailure)
    {
        var proof = await VerifyProof(await RequestAsync(Phone));
        _failure.Enabled = !userSaveFailure;
        _failure.FailUser = userSaveFailure;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await RegisterResponse(proof.RegistrationToken!)).StatusCode);
        using (var scope = _factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Empty(await db.Users.ToListAsync()); Assert.Empty(await db.AuthSessions.ToListAsync());
            Assert.Null((await db.OtpChallenges.SingleAsync()).ConsumedAtUtc);
        }
        _failure.Enabled = false; _failure.FailUser = false;
        Assert.Equal(HttpStatusCode.OK, (await RegisterResponse(proof.RegistrationToken!)).StatusCode);
    }

    [Fact]
    public async Task Incorrect_expired_and_reused_codes_do_not_verify()
    {
        var challenge = await RequestAsync(Phone);
        var wrong = _sms.Code == "000000" ? "111111" : "000000";
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.ChallengeId, wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(challenge.ChallengeId, _sms.Code)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.ChallengeId, _sms.Code)).StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(61));
        var next = await RequestAsync(Phone); _clock.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(next.ChallengeId, _sms.Code)).StatusCode);
    }

    [Fact]
    public async Task Five_failed_attempts_are_durable_and_exhaust_the_challenge()
    {
        var challenge = await RequestAsync(Phone); var wrong = _sms.Code == "000000" ? "111111" : "000000";
        for (var index = 0; index < 5; index++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.ChallengeId, wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.ChallengeId, _sms.Code)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(5, (await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().OtpChallenges.SingleAsync()).Attempts);
    }

    [Fact]
    public async Task Resend_cooldown_and_hourly_sms_quota_are_preserved()
    {
        var first = await RequestAsync(Phone); var oldCode = _sms.Code;
        var limited = await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = Phone });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.Equal(TimeSpan.FromSeconds(60), limited.Headers.RetryAfter!.Delta);
        for (var index = 1; index < 5; index++) { _clock.Advance(TimeSpan.FromSeconds(61)); await RequestAsync(Phone); }
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(first.ChallengeId, oldCode)).StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = Phone })).StatusCode);
        Assert.Equal(5, _sms.Sends);
    }

    [Fact]
    public async Task Failed_delivery_never_produces_usable_otp_proof()
    {
        _sms.Configured = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = Phone })).StatusCode);
        _sms.Configured = true; _sms.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = Phone })).StatusCode);
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var challenge = await db.OtpChallenges.SingleAsync(); Assert.False(challenge.Delivered);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VerifyAsync(challenge.Id, _sms.Code)).StatusCode);
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Theory]
    [InlineData("authenticate")] [InlineData("login")] [InlineData("activate")] [InlineData("recover")]
    public async Task Otp_cannot_be_used_as_passwordless_login(string purpose)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = Phone, purpose })).StatusCode);
        Assert.Equal(0, _sms.Sends);
    }

    [Theory]
    [InlineData("02121234567")] [InlineData("+14155552671")] [InlineData("+98abc1234567")]
    public async Task Invalid_sms_numbers_are_rejected_without_delivery(string phone)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = phone })).StatusCode);
        Assert.Equal(0, _sms.Sends);
    }

    [Fact]
    public async Task Persian_digits_normalize_and_codes_are_six_digits()
    {
        var challenge = await RequestAsync("۰۹۱۲۱۲۳۴۵۶۷");
        Assert.Equal(Phone, _sms.Phone); Assert.Matches("^[0-9]{6}$", _sms.Code); Assert.Equal(6, challenge.CodeLength);
    }

    private async Task<OtpRequestResponse> RequestAsync(string phone, string purpose = "register")
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request", new { phoneNumber = phone, purpose });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return (await response.Content.ReadFromJsonAsync<OtpRequestResponse>())!;
    }
    private Task<HttpResponseMessage> VerifyAsync(Guid challengeId, string code) => _client.PostAsJsonAsync("/api/v1/auth/phone/otp/verify", new { challengeId, code });
    private async Task<OtpVerificationResponse> VerifyProof(OtpRequestResponse challenge) {
        var response = await VerifyAsync(challenge.ChallengeId, _sms.Code); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OtpVerificationResponse>())!;
    }
    private Task<HttpResponseMessage> RegisterResponse(string proof, string phone = Phone) =>
        _client.PostAsJsonAsync("/api/v1/auth/phone/register", new { phoneNumber = phone, registrationToken = proof, password = Password, confirmPassword = Password, username = "Student" });
    private async Task<AuthTokens> Register(string proof) {
        var response = await RegisterResponse(proof); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
    }
    private Task<HttpResponseMessage> Login(string phone = Phone, string password = Password) =>
        _client.PostAsJsonAsync("/api/v1/auth/phone/login", new { phoneNumber = phone, password });
    private Task<HttpResponseMessage> Reset(string proof, string password = "New private password2026!", string phone = Phone) =>
        _client.PostAsJsonAsync("/api/v1/auth/phone/password/reset", new { phoneNumber = phone, resetToken = proof, password, confirmPassword = password });

    private sealed class TestClock : TimeProvider {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
    private sealed class TestSmsSender : ISmsOtpSender {
        public bool Configured { get; set; } = true; public bool Fail { get; set; }
        public bool IsConfigured => Configured; public int Sends { get; private set; }
        public string Code { get; private set; } = ""; public string Phone { get; private set; } = "";
        public Task SendAsync(string phone, string code, CancellationToken ct) {
            Phone = phone; Code = code; Sends++; if (Fail) throw new PhoneAuthException(503, "sms_unavailable", "SMS unavailable.");
            return Task.CompletedTask;
        }
    }
    private sealed class SessionFailure : SaveChangesInterceptor {
        public bool Enabled { get; set; }
        public bool FailUser { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) {
            if (FailUser && data.Context!.ChangeTracker.Entries<User>().Any(x => x.State == EntityState.Added))
                throw new DbUpdateException("Injected isolated account storage failure.");
            if (Enabled && data.Context!.ChangeTracker.Entries<AuthSession>().Any(x => x.State == EntityState.Added))
                throw new DbUpdateException("Injected isolated session storage failure.");
            return base.SavingChangesAsync(data, result, ct);
        }
    }
    private sealed class PhoneApiFactory(SqliteConnection connection, ISmsOtpSender sender, TimeProvider clock, SessionFailure failure) : TestApiFactory {
        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                services.RemoveAll<DbContextOptions<FlashcardsDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<FlashcardsDbContext>>();
                services.AddDbContext<FlashcardsDbContext>(options => options.UseSqlite(connection).AddInterceptors(failure));
                services.RemoveAll<ISmsOtpSender>(); services.AddSingleton(sender);
                services.RemoveAll<TimeProvider>(); services.AddSingleton(clock);
            });
        }
    }
}
