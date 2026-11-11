using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Flashcards.Tests;

public sealed class PhoneVerificationMigrationTests
{
    [Fact]
    public void Session_compatibility_migration_preserves_existing_password_sessions_and_cent()
    {
        using var database = new FlashcardsDbContext(new DbContextOptionsBuilder<FlashcardsDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=NotOpened;Trusted_Connection=True").Options);
        var script = database.GetService<IMigrator>().GenerateScript("20261110145342_RequireVerifiedPhoneAuthentication",
            "20261110182603_SmsOtpOnlySessions", MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("[OtpAuthenticated]", script);
        Assert.DoesNotContain("UPDATE [AuthSessions]", script);
        Assert.DoesNotContain("UPDATE [OtpChallenges]", script);
        Assert.DoesNotContain("cent_", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE [Users]", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.False(database.Database.HasPendingModelChanges());
    }
    [Fact]
    public void Verification_migration_is_additive_preserves_sessions_and_does_not_guess_verified_users()
    {
        using var database = new FlashcardsDbContext(new DbContextOptionsBuilder<FlashcardsDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=NotOpened;Trusted_Connection=True").Options);
        var script = database.GetService<IMigrator>().GenerateScript("20261102105010_AddSm2ReviewSystem",
            "20261110145342_RequireVerifiedPhoneAuthentication", MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("[PhoneVerifiedAtUtc]", script);
        Assert.Contains("[VerifiedPhoneNumber]", script);
        Assert.Contains("[ActivationUserId]", script);
        Assert.DoesNotContain("UPDATE s SET [RevokedAtUtc]", script);
        Assert.DoesNotContain("cent_", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP INDEX", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MyketOrders", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReviewStates", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE [Users]", script, StringComparison.OrdinalIgnoreCase);
        Assert.False(database.Database.HasPendingModelChanges());
    }
}
