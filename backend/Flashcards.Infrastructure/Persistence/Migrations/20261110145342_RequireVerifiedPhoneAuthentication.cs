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

            // Historical phone/email/Google sessions are not evidence of SMS ownership.
            // Do not guess verification or alter user IDs/content. Revoke legacy access.
            migrationBuilder.Sql("""
                UPDATE s SET [RevokedAtUtc] = SYSUTCDATETIME()
                FROM [AuthSessions] AS s
                INNER JOIN [Users] AS u ON u.[Id] = s.[UserId]
                WHERE s.[RevokedAtUtc] IS NULL AND u.[PhoneVerifiedAtUtc] IS NULL;
                """);
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
