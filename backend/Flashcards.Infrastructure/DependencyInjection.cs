using System.IdentityModel.Tokens.Jwt;
using Flashcards.Domain;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Sync;
using Flashcards.Infrastructure.Backups;
using Flashcards.Infrastructure.Subscriptions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Flashcards.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = AuthSettings.FromConfiguration(configuration);
        services.AddSingleton(auth);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = auth.Issuer,
                ValidateAudience = true,
                ValidAudience = auth.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(auth.SigningKey),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var subject = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                    var session = context.Principal?.FindFirst("sid")?.Value;
                    if (!Guid.TryParse(subject, out var userId) || !Guid.TryParse(session, out var sessionId) ||
                        !await context.HttpContext.RequestServices.GetRequiredService<TokenService>()
                            .IsSessionActiveAsync(userId, sessionId, context.HttpContext.RequestAborted))
                        context.Fail("Session is no longer active.");
                }
            };
        });
        services.AddAuthorization();
        services.AddScoped<FlashcardsQueries>();
        services.AddScoped<UserService>();
        services.AddScoped<TokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<PhoneAuthService>();
        services.AddScoped<GoogleAuthService>();
        services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(_ =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                "https://accounts.google.com/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }) { RequireHttps = true }));
        services.AddSingleton<IGoogleTokenVerifier, GoogleTokenVerifier>();
        services.AddSingleton(TimeProvider.System);
        var smsProvider = configuration["Sms:Provider"] ?? "Webhook";
        if (string.Equals(smsProvider, "SmsIr", StringComparison.OrdinalIgnoreCase))
            services.AddHttpClient<ISmsOtpSender, SmsIrOtpSender>(client => client.Timeout = TimeSpan.FromSeconds(10))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
                .RedactLoggedHeaders(["x-api-key", "Authorization"]);
        else if (string.Equals(smsProvider, "Webhook", StringComparison.OrdinalIgnoreCase))
            services.AddHttpClient<ISmsOtpSender, WebhookSmsOtpSender>(client => client.Timeout = TimeSpan.FromSeconds(10))
                .RedactLoggedHeaders(["Authorization"]);
        else
            throw new InvalidOperationException("Sms:Provider must be SmsIr or Webhook.");
        services.AddScoped<SyncService>();
        services.AddSingleton<PlanCatalog>();
        services.AddSingleton(MyketSettings.FromConfiguration(configuration));
        services.AddScoped<MyketPaymentService>();
        services.AddHttpClient<IMyketVerifier, MyketVerifier>(client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RedactLoggedHeaders(["X-Access-Token", "Authorization"]);
        services.AddScoped<EntitlementService>();
        var backups = BackupSettings.FromConfiguration(configuration);
        services.AddSingleton(backups);
        var protection = services.AddDataProtection().SetApplicationName("Lerona.Backups");
        if (!string.IsNullOrWhiteSpace(backups.KeyRingPath) && Path.IsPathFullyQualified(backups.KeyRingPath))
        {
            protection.PersistKeysToFileSystem(new DirectoryInfo(backups.KeyRingPath));
            if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        }
        services.AddSingleton<IBackupPayloadProtector, BackupPayloadProtector>();
        services.AddScoped<BackupService>();
        services.AddScoped<BackupRestoreService>();
        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
        var connectionString = configuration.GetConnectionString("FlashcardsDb");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<FlashcardsDbContext>(options => options.UseSqlServer(connectionString));
        }

        return services;
    }
}
