using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flashcards.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Plan = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.CheckConstraint("CK_Subscriptions_Period", "[ExpiresAtUtc] > [StartsAtUtc]");
                    table.CheckConstraint("CK_Subscriptions_Plan", "[Plan] IN ('Plus', 'Full')");
                    table.CheckConstraint("CK_Subscriptions_Status", "[Status] IN ('Active', 'Expired', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_Subscriptions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_UserId_CreatedAtUtc_Id",
                table: "Subscriptions",
                columns: new[] { "UserId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_Subscriptions_User_Active",
                table: "Subscriptions",
                column: "UserId",
                unique: true,
                filter: "[Status] = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Subscriptions");
        }
    }
}
