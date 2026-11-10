using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
    public async Task New_user_is_created_only_after_sms_and_without_password_email_or_google()
    {
        var challenge = await RequestAsync(Phone);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Empty(await db.Users.ToListAsync()); Assert.Empty(await db.AuthSessions.ToListAsync());
            Assert.Empty(await db.RefreshTokens.ToListAsync());
            Assert.NotEqual(_sms.Code, (await db.OtpChallenges.SingleAsync()).CodeHash);
        }
        var tokens = await AuthenticateAsync(challenge);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/me/")).StatusCode);
        using var final = _factory.Services.CreateScope(); var database = final.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var user = await database.Users.SingleAsync();
        Assert.Equal(Phone, user.PhoneNumber); Assert.Equal(Phone, user.VerifiedPhoneNumber);
        Assert.NotNull(user.PhoneVerifiedAtUtc); Assert.Null(user.PasswordHash); Assert.Null(user.Email); Assert.Null(user.GoogleSubject);
        Assert.Single(await database.SyncHeads.ToListAsync()); Assert.True((await database.AuthSessions.SingleAsync()).OtpAuthenticated);
        Assert.NotEqual(tokens.RefreshToken, (await database.RefreshTokens.SingleAsync()).TokenHash);
        Assert.NotNull((await database.OtpChallenges.SingleAsync()).ConsumedAtUtc);
    }
    [Fact]
    public async Task Existing_verified_user_logs_into_original_account_without_changing_relationships()
    {
        var user = await SeedLegacyAsync(verified: true);
        using (var scope = _factory.Services.CreateScope()) { var db=scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>(); db.Decks.Add(new Deck { UserId=user.Id, Name="Preserved" }); await db.SaveChangesAsync(); }
        var tokens = await AuthenticateAsync(await RequestAsync(Phone));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(user.Id, (await _client.GetFromJsonAsync<JsonElement>("/api/v1/me/" )).GetProperty("id").GetGuid());
        using var final = _factory.Services.CreateScope(); var database=final.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var saved=await database.Users.SingleAsync(); Assert.Equal(user.PasswordHash,saved.PasswordHash); Assert.Equal(user.DisplayName,saved.DisplayName);
        Assert.Equal(user.Id,(await database.Decks.SingleAsync()).UserId);
    }
    [Theory]
    [InlineData("/api/v1/auth/register")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/auth/google")]
    [InlineData("/api/v1/auth/google/login")]
    [InlineData("/api/v1/auth/google/register")]
    [InlineData("/api/v1/auth/phone/register")]
    [InlineData("/api/v1/auth/phone/register-password")]
    [InlineData("/api/v1/auth/phone/login")]
    [InlineData("/api/v1/auth/phone/activation/request")]
    public async Task All_legacy_paths_are_disabled_even_with_valid_credentials_and_client_flags(string path)
    {
        await SeedLegacyAsync(verified:true);
        var response = await _client.PostAsJsonAsync(path,new { phoneNumber=Phone,password=Password,confirmPassword=Password,email="legacy@example.com",displayName="Learner",username="Learner",idToken="token",registrationToken="proof",phoneVerified=true,verifiedPhoneNumber=Phone,phoneVerifiedAtUtc=DateTimeOffset.UtcNow });
        Assert.Equal(HttpStatusCode.Gone,response.StatusCode); Assert.Equal("sms_otp_only",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var scope=_factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Empty(await db.AuthSessions.ToListAsync());Assert.Empty(await db.RefreshTokens.ToListAsync());Assert.Equal(0,_sms.Sends);
    }
    [Fact]
    public async Task Invalid_expired_and_reused_codes_cannot_issue_tokens()
    {
        var challenge=await RequestAsync(Phone);var wrong=_sms.Code=="000000"?"111111":"000000";
        Assert.Equal(HttpStatusCode.Unauthorized,(await VerifyAsync(challenge.ChallengeId,wrong)).StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(120));Assert.Equal(HttpStatusCode.Unauthorized,(await VerifyAsync(challenge.ChallengeId,_sms.Code)).StatusCode);
        challenge=await RequestAsync(Phone);await AuthenticateAsync(challenge);
        Assert.Equal(HttpStatusCode.Unauthorized,(await VerifyAsync(challenge.ChallengeId,_sms.Code)).StatusCode);
        using var scope=_factory.Services.CreateScope();Assert.Single(await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().AuthSessions.ToListAsync());
    }
    [Fact]
    public async Task Five_failed_attempts_are_durable_and_exhaust_the_challenge()
    {
        var challenge=await RequestAsync(Phone);var wrong=_sms.Code=="000000"?"111111":"000000";
        for(var i=0;i<5;i++)Assert.Equal(HttpStatusCode.Unauthorized,(await VerifyAsync(challenge.ChallengeId,wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await VerifyAsync(challenge.ChallengeId,_sms.Code)).StatusCode);
        using var scope=_factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();Assert.Equal(5,(await db.OtpChallenges.SingleAsync()).Attempts);Assert.Empty(await db.AuthSessions.ToListAsync());
    }
    [Fact]
    public async Task Resend_cooldown_hourly_limit_and_old_code_invalidation_are_enforced()
    {
        var first=await RequestAsync(Phone);var code=_sms.Code;
        var limited=await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request",new {phoneNumber="09121234567"});Assert.Equal(HttpStatusCode.TooManyRequests,limited.StatusCode);Assert.Equal(TimeSpan.FromSeconds(60),limited.Headers.RetryAfter!.Delta);
        for(var i=1;i<5;i++){_clock.Advance(TimeSpan.FromSeconds(61));await RequestAsync(Phone);}
        Assert.Equal(HttpStatusCode.Unauthorized,(await VerifyAsync(first.ChallengeId,code)).StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(61));Assert.Equal(HttpStatusCode.TooManyRequests,(await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request",new {phoneNumber=Phone})).StatusCode);Assert.Equal(5,_sms.Sends);
    }
    [Fact]
    public async Task Refresh_rotation_replay_and_logout_preserve_security()
    {
        var original=await AuthenticateAsync(await RequestAsync(Phone));
        var rotatedResponse=await _client.PostAsJsonAsync("/api/v1/auth/refresh",new {refreshToken=original.RefreshToken});Assert.Equal(HttpStatusCode.OK,rotatedResponse.StatusCode);
        var rotated=(await rotatedResponse.Content.ReadFromJsonAsync<AuthTokens>())!;Assert.NotEqual(original.RefreshToken,rotated.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized,(await _client.PostAsJsonAsync("/api/v1/auth/refresh",new {refreshToken=original.RefreshToken})).StatusCode);
        _client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",rotated.AccessToken);Assert.Equal(HttpStatusCode.Unauthorized,(await _client.GetAsync("/api/v1/me/")).StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(61));var next=await AuthenticateAsync(await RequestAsync(Phone));_client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",next.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent,(await _client.PostAsync("/api/v1/auth/logout",null)).StatusCode);Assert.Equal(HttpStatusCode.Unauthorized,(await _client.GetAsync("/api/v1/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await _client.PostAsJsonAsync("/api/v1/auth/refresh",new {refreshToken=next.RefreshToken})).StatusCode);
    }
    [Theory]
    [InlineData("register")][InlineData("login")][InlineData("activate")][InlineData("recover")]
    public async Task Old_and_recovery_operations_cannot_be_requested_by_clients(string purpose)
    { Assert.Equal(HttpStatusCode.BadRequest,(await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request",new {phoneNumber=Phone,purpose,phoneVerified=true,activationUserId=Guid.NewGuid()})).StatusCode);Assert.Equal(0,_sms.Sends); }
    [Theory]
    [InlineData("02121234567")][InlineData("+14155552671")][InlineData("+98abc1234567")]
    public async Task Invalid_numbers_are_rejected_without_sms(string phoneNumber)
    { Assert.Equal(HttpStatusCode.BadRequest,(await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request",new {phoneNumber})).StatusCode);Assert.Equal(0,_sms.Sends); }
    [Fact]
    public async Task Persian_phone_digits_are_normalized_and_code_is_six_digits()
    { var c=await RequestAsync("۰۹۱۲۱۲۳۴۵۶۷");Assert.Equal(Phone,_sms.PhoneNumber);Assert.Matches("^[0-9]{6}$",_sms.Code);Assert.Equal(6,c.CodeLength);Assert.Equal(TimeSpan.FromSeconds(120),c.ExpiresAtUtc-_clock.GetUtcNow()); }
    [Fact]
    public async Task Phone_and_operation_are_bound_to_the_hash()
    {
        var c=await RequestAsync(Phone);using(var scope=_factory.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().OtpChallenges.Where(x=>x.Id==c.ChallengeId).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.PhoneNumber,"+989121234568"));
        Assert.Equal(HttpStatusCode.Unauthorized,(await VerifyAsync(c.ChallengeId,_sms.Code)).StatusCode);
    }
    [Fact]
    public async Task Failed_delivery_is_not_verifiable_and_returns_no_tokens()
    {
        _sms.Configured=false;Assert.Equal(HttpStatusCode.ServiceUnavailable,(await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request",new {phoneNumber=Phone})).StatusCode);
        _sms.Configured=true;_sms.Fail=true;Assert.Equal(HttpStatusCode.ServiceUnavailable,(await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request",new {phoneNumber=Phone})).StatusCode);
        using var scope=_factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();var c=await db.OtpChallenges.SingleAsync();Assert.False(c.Delivered);Assert.Equal(HttpStatusCode.Unauthorized,(await VerifyAsync(c.Id,_sms.Code)).StatusCode);Assert.Empty(await db.AuthSessions.ToListAsync());
    }
    private async Task<OtpRequestResponse> RequestAsync(string phoneNumber)
    { var response=await _client.PostAsJsonAsync("/api/v1/auth/phone/otp/request",new {phoneNumber});Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("no-store",response.Headers.CacheControl?.ToString());return (await response.Content.ReadFromJsonAsync<OtpRequestResponse>())!; }
    private Task<HttpResponseMessage> VerifyAsync(Guid challengeId,string code)=>_client.PostAsJsonAsync("/api/v1/auth/phone/otp/verify",new {challengeId,code});
    private async Task<AuthTokens> AuthenticateAsync(OtpRequestResponse challenge)
    { var response=await VerifyAsync(challenge.ChallengeId,_sms.Code);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var result=(await response.Content.ReadFromJsonAsync<OtpVerificationResponse>())!;Assert.Null(result.RegistrationToken);Assert.NotNull(result.Tokens);return result.Tokens; }

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

    private sealed class PhoneApiFactory(SqliteConnection connection, ISmsOtpSender sender, TimeProvider clock, OtpSettings? otp = null) : TestApiFactory
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
                if (otp is not null)
                {
                    services.RemoveAll<OtpSettings>();
                    services.AddSingleton(otp);
                }
            });
        }
    }
}
