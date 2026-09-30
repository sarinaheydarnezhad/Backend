using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flashcards.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SyncVersion",
                table: "UserSettings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SyncVersion",
                table: "Decks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SyncVersion",
                table: "Cards",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SyncVersion",
                table: "CardReviewStates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "SyncChanges",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientChangeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ServerChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    EntityType = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    ExpectedVersion = table.Column<long>(type: "bigint", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Fingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncChanges", x => new { x.UserId, x.Version });
                    table.CheckConstraint("CK_SyncChanges_Payload", "ISJSON([PayloadJson]) = 1");
                    table.CheckConstraint("CK_SyncChanges_Version", "[Version] > 0 AND [ExpectedVersion] >= 0");
                    table.ForeignKey(
                        name: "FK_SyncChanges_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SyncHeads",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncHeads", x => x.UserId);
                    table.CheckConstraint("CK_SyncHeads_Version", "[Version] >= 0");
                    table.ForeignKey(
                        name: "FK_SyncHeads_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.Sql("INSERT INTO [SyncHeads] ([UserId], [Version]) SELECT [Id], 0 FROM [Users]");

            migrationBuilder.CreateIndex(
                name: "IX_SyncChanges_UserId_DeviceId_ClientChangeId",
                table: "SyncChanges",
                columns: new[] { "UserId", "DeviceId", "ClientChangeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncChanges_UserId_EntityType_EntityId_Version",
                table: "SyncChanges",
                columns: new[] { "UserId", "EntityType", "EntityId", "Version" });

            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER [TR_SyncChanges_Immutable] ON [SyncChanges]
                AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50002, ''Sync changes are immutable.'', 1;
                END')
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER [TR_SyncChanges_Immutable]");

            migrationBuilder.DropTable(
                name: "SyncChanges");

            migrationBuilder.DropTable(
                name: "SyncHeads");

            migrationBuilder.DropColumn(
                name: "SyncVersion",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "SyncVersion",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "SyncVersion",
                table: "Cards");

            migrationBuilder.DropColumn(
                name: "SyncVersion",
                table: "CardReviewStates");
        }
    }
}
