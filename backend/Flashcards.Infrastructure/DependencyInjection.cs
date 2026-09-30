using System.IdentityModel.Tokens.Jwt;
using Flashcards.Domain;
using Flashcards.Infrastructure.Auth;
using Flashcards.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

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
        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
        var connectionString = configuration.GetConnectionString("FlashcardsDb");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<FlashcardsDbContext>(options => options.UseSqlServer(connectionString));
        }

        return services;
    }
}
