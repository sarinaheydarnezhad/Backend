using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flashcards.Infrastructure.Persistence.Migrations;

public partial class OptimizeBackupReadIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_UserBackups_UserId_CreatedAtUtc_Id", table: "UserBackups");
        migrationBuilder.CreateIndex(
            name: "IX_UserBackups_UserId_CreatedAtUtc_Id",
            table: "UserBackups",
            columns: new[] { "UserId", "CreatedAtUtc", "Id" },
            descending: new[] { false, true, false })
            .Annotation("SqlServer:Include", new[] { "Status", "FormatVersion", "CompletedAtUtc", "ExpiresAtUtc", "RetentionDays", "SyncVersion", "SizeBytes", "FailureCode" });
        migrationBuilder.CreateIndex(
            name: "IX_UserBackups_UserId_FormatVersion_CompletedAtUtc_Id",
            table: "UserBackups",
            columns: new[] { "UserId", "FormatVersion", "CompletedAtUtc", "Id" },
            descending: new[] { false, false, true, false },
            filter: "[Status] = 'Completed'")
            .Annotation("SqlServer:Include", new[] { "Status", "CreatedAtUtc", "ExpiresAtUtc", "RetentionDays", "SyncVersion", "SizeBytes", "FailureCode" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_UserBackups_UserId_FormatVersion_CompletedAtUtc_Id", table: "UserBackups");
        migrationBuilder.DropIndex(name: "IX_UserBackups_UserId_CreatedAtUtc_Id", table: "UserBackups");
        migrationBuilder.CreateIndex(
            name: "IX_UserBackups_UserId_CreatedAtUtc_Id",
            table: "UserBackups",
            columns: new[] { "UserId", "CreatedAtUtc", "Id" });
    }
}
