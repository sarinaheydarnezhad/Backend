using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flashcards.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SmsOtpOnlySessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OtpAuthenticated",
                table: "AuthSessions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // No historical password/Google session is evidence of OTP authentication.
            migrationBuilder.Sql("UPDATE [AuthSessions] SET [RevokedAtUtc] = SYSUTCDATETIME() WHERE [OtpAuthenticated] = 0 AND [RevokedAtUtc] IS NULL;");
            migrationBuilder.Sql("UPDATE [OtpChallenges] SET [ConsumedAtUtc] = SYSUTCDATETIME() WHERE [Purpose] <> 'authenticate' AND [ConsumedAtUtc] IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OtpAuthenticated",
                table: "AuthSessions");
        }
    }
}
