using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cent.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddCentGoogleAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GoogleSubject",
                table: "cent_users",
                type: "varchar(255)",
                unicode: false,
                maxLength: 255,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateTable(
                name: "cent_google_attempts",
                columns: table => new
                {
                    StateHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    VerifierHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    NonceHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_google_attempts", x => x.StateHash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cent_users_GoogleSubject",
                table: "cent_users",
                column: "GoogleSubject",
                unique: true,
                filter: "[GoogleSubject] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_cent_google_attempts_ExpiresAt",
                table: "cent_google_attempts",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cent_google_attempts");

            migrationBuilder.DropIndex(
                name: "IX_cent_users_GoogleSubject",
                table: "cent_users");

            migrationBuilder.DropColumn(
                name: "GoogleSubject",
                table: "cent_users");
        }
    }
}
