using Cent.Module;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.RegularExpressions;

namespace Cent.Exam.Tests;

public sealed class ProductionPrerequisiteTests
{
    [Fact]
    public void Complete_Cent_SQL_Server_chain_generates_offline_and_matches_current_model()
    {
        using var db = new CentDbContext(new DbContextOptionsBuilder<CentDbContext>().UseSqlServer(
            "Server=offline.invalid;Database=NeverConnect;Integrated Security=true;TrustServerCertificate=true",
            sql => sql.MigrationsHistoryTable("cent_migrations_history")).Options);
        var migrations = db.Database.GetMigrations().ToArray();
        Assert.Equal(9, migrations.Length);
        Assert.Equal("20261124174931_ReaderProtectionBudgets", migrations[^1]);
        Assert.False(db.GetService<IMigrator>().HasPendingModelChanges());
        var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        foreach (Match table in Regex.Matches(script, @"(?:CREATE TABLE|ALTER TABLE|INSERT INTO|UPDATE|DELETE FROM|DROP TABLE) \[([^\]]+)\]"))
            Assert.StartsWith("cent_", table.Groups[1].Value);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("DELETE FROM", script);
        Assert.Contains("[cent_migrations_history]", script);
        Assert.DoesNotContain("[__EFMigrationsHistory]", script);
        Assert.Contains("[cent_subject_offers]", script);
        Assert.Contains("[cent_curriculum_revisions]", script);
        Assert.Contains("[cent_mobile_identities]", script);
        Assert.Contains("'SUBJECT'", script);
        Assert.DoesNotContain("Lerona", script);
        Assert.DoesNotContain("Flashcards", script);
        // Optional review artifact only; this test never opens a SQL Server connection.
        var output = Environment.GetEnvironmentVariable("CENT_SQL_AUDIT_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output)) File.WriteAllText(output, script);
    }
}
