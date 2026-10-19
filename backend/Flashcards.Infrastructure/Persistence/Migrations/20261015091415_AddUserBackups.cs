using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flashcards.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserBackups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    FormatVersion = table.Column<int>(type: "int", nullable: false),
                    RequestedSyncVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RetentionDays = table.Column<int>(type: "int", nullable: false),
                    SyncVersion = table.Column<long>(type: "bigint", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Payload = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    FailureCode = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserBackups", x => x.Id);
                    table.CheckConstraint("CK_UserBackups_Completion", "([Status] = 'Completed' AND [Payload] IS NOT NULL AND [SizeBytes] IS NOT NULL AND [SizeBytes] > 0 AND [CompletedAtUtc] IS NOT NULL AND [SyncVersion] IS NOT NULL AND [FailureCode] IS NULL) OR ([Status] <> 'Completed' AND [Payload] IS NULL AND [SizeBytes] IS NULL AND [CompletedAtUtc] IS NULL)");
                    table.CheckConstraint("CK_UserBackups_Retention", "[RetentionDays] BETWEEN 1 AND 3650 AND [ExpiresAtUtc] > [CreatedAtUtc] AND [FormatVersion] > 0 AND [RequestedSyncVersion] >= 0");
                    table.CheckConstraint("CK_UserBackups_Status", "[Status] IN ('Pending', 'Completed', 'Failed')");
                    table.ForeignKey(
                        name: "FK_UserBackups_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserBackups_ExpiresAtUtc",
                table: "UserBackups",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UserBackups_UserId_CreatedAtUtc_Id",
                table: "UserBackups",
                columns: new[] { "UserId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_UserBackups_UserId_RequestId",
                table: "UserBackups",
                columns: new[] { "UserId", "RequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserBackups");
        }
    }
}
