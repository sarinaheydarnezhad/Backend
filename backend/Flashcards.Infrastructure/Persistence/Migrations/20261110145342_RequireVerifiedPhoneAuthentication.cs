using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flashcards.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RequireVerifiedPhoneAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PhoneVerifiedAtUtc",
                table: "Users",
                type: "datetimeoffset(7)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedPhoneNumber",
                table: "Users",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActivationUserId",
                table: "OtpChallenges",
                type: "uniqueidentifier",
                nullable: true);

            // SMS is required for new signup and password recovery. Existing password
            // accounts and sessions keep working; do not guess historical phone verification.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PhoneVerifiedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "VerifiedPhoneNumber",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ActivationUserId",
                table: "OtpChallenges");
        }
    }
}
