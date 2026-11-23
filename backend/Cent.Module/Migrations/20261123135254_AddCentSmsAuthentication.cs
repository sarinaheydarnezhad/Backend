using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cent.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddCentSmsAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LinkUserId",
                table: "cent_google_attempts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cent_mobile_identities",
                columns: table => new
                {
                    Phone = table.Column<string>(type: "varchar(13)", unicode: false, maxLength: 13, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_mobile_identities", x => x.Phone);
                    table.ForeignKey(
                        name: "FK_cent_mobile_identities_cent_users_UserId",
                        column: x => x.UserId,
                        principalTable: "cent_users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_sms_budgets",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    ResetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_sms_budgets", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "cent_sms_challenges",
                columns: table => new
                {
                    IdHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Phone = table.Column<string>(type: "varchar(13)", unicode: false, maxLength: 13, nullable: false),
                    Verifier = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    LinkUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResendAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Delivered = table.Column<bool>(type: "bit", nullable: false),
                    Consumed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_sms_challenges", x => x.IdHash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cent_mobile_identities_UserId",
                table: "cent_mobile_identities",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_sms_budgets_ResetAt",
                table: "cent_sms_budgets",
                column: "ResetAt");

            migrationBuilder.CreateIndex(
                name: "IX_cent_sms_challenges_ExpiresAt",
                table: "cent_sms_challenges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_cent_sms_challenges_Phone",
                table: "cent_sms_challenges",
                column: "Phone");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cent_mobile_identities");

            migrationBuilder.DropTable(
                name: "cent_sms_budgets");

            migrationBuilder.DropTable(
                name: "cent_sms_challenges");

            migrationBuilder.DropColumn(
                name: "LinkUserId",
                table: "cent_google_attempts");
        }
    }
}
