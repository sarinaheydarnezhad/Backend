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

            // Retain this additive compatibility column, but do not require it for
            // password sessions or revoke existing users during the planned upgrade.
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
