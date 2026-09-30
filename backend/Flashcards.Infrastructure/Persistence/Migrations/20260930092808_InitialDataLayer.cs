using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flashcards.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialDataLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ArchivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Decks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Language = table.Column<string>(type: "varchar(35)", unicode: false, maxLength: 35, nullable: false),
                    TextAlignment = table.Column<string>(type: "varchar(6)", unicode: false, maxLength: 6, nullable: false),
                    TypographySize = table.Column<string>(type: "varchar(6)", unicode: false, maxLength: 6, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ArchivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Decks", x => x.Id);
                    table.UniqueConstraint("AK_Decks_UserId_Id", x => new { x.UserId, x.Id });
                    table.CheckConstraint("CK_Decks_TextAlignment", "[TextAlignment] IN ('ltr', 'rtl', 'center')");
                    table.CheckConstraint("CK_Decks_TypographySize", "[TypographySize] IN ('small', 'medium', 'large')");
                    table.ForeignKey(
                        name: "FK_Decks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "UserSettings",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Theme = table.Column<string>(type: "varchar(6)", unicode: false, maxLength: 6, nullable: false),
                    HapticsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Language = table.Column<string>(type: "varchar(35)", unicode: false, maxLength: 35, nullable: false),
                    DailyReminderEnabled = table.Column<bool>(type: "bit", nullable: false),
                    DailyReminderTime = table.Column<TimeOnly>(type: "time(0)", nullable: true),
                    PreferredSpeechLanguage = table.Column<string>(type: "varchar(35)", unicode: false, maxLength: 35, nullable: false),
                    PreferredSpeechAccent = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSettings", x => x.UserId);
                    table.CheckConstraint("CK_UserSettings_Accent", "[PreferredSpeechAccent] IS NULL OR [PreferredSpeechAccent] IN ('us', 'uk')");
                    table.CheckConstraint("CK_UserSettings_Reminder", "[DailyReminderEnabled] = 0 OR [DailyReminderTime] IS NOT NULL");
                    table.CheckConstraint("CK_UserSettings_Theme", "[Theme] IN ('system', 'light', 'dark', 'oled')");
                    table.ForeignKey(
                        name: "FK_UserSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Cards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeckId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FrontText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Phonetic = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Meaning = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ExamplesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ArchivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cards", x => x.Id);
                    table.UniqueConstraint("AK_Cards_DeckId_Id", x => new { x.DeckId, x.Id });
                    table.CheckConstraint("CK_Cards_ExamplesJson", "ISJSON([ExamplesJson]) = 1");
                    table.ForeignKey(
                        name: "FK_Cards_Decks_DeckId",
                        column: x => x.DeckId,
                        principalTable: "Decks",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CardReviewStates",
                columns: table => new
                {
                    CardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Box = table.Column<int>(type: "int", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LastReviewedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConsecutiveSuccesses = table.Column<int>(type: "int", nullable: false),
                    TotalReviews = table.Column<int>(type: "int", nullable: false),
                    TotalSuccesses = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardReviewStates", x => x.CardId);
                    table.CheckConstraint("CK_ReviewStates_Box", "[Box] BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_ReviewStates_Counts", "[ConsecutiveSuccesses] >= 0 AND [TotalReviews] >= 0 AND [TotalSuccesses] >= 0 AND [TotalSuccesses] <= [TotalReviews] AND [ConsecutiveSuccesses] <= [TotalSuccesses]");
                    table.ForeignKey(
                        name: "FK_CardReviewStates_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ReviewEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeckId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousBox = table.Column<int>(type: "int", nullable: false),
                    NewBox = table.Column<int>(type: "int", nullable: false),
                    Result = table.Column<string>(type: "varchar(7)", unicode: false, maxLength: 7, nullable: false),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    StudySessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewEvents", x => x.Id);
                    table.CheckConstraint("CK_ReviewEvents_Boxes", "[PreviousBox] BETWEEN 1 AND 5 AND [NewBox] BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_ReviewEvents_Result", "[Result] IN ('success', 'failure')");
                    table.ForeignKey(
                        name: "FK_ReviewEvents_Cards_DeckId_CardId",
                        columns: x => new { x.DeckId, x.CardId },
                        principalTable: "Cards",
                        principalColumns: new[] { "DeckId", "Id" });
                    table.ForeignKey(
                        name: "FK_ReviewEvents_Decks_UserId_DeckId",
                        columns: x => new { x.UserId, x.DeckId },
                        principalTable: "Decks",
                        principalColumns: new[] { "UserId", "Id" });
                    table.ForeignKey(
                        name: "FK_ReviewEvents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardReviewStates_DueDate_CardId",
                table: "CardReviewStates",
                columns: new[] { "DueDate", "CardId" });

            migrationBuilder.CreateIndex(
                name: "IX_CardReviewStates_UpdatedAtUtc_CardId",
                table: "CardReviewStates",
                columns: new[] { "UpdatedAtUtc", "CardId" });

            migrationBuilder.CreateIndex(
                name: "IX_Cards_DeckId_ArchivedAtUtc_Id",
                table: "Cards",
                columns: new[] { "DeckId", "ArchivedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Cards_DeckId_UpdatedAtUtc_Id",
                table: "Cards",
                columns: new[] { "DeckId", "UpdatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Decks_UserId_ArchivedAtUtc_Name",
                table: "Decks",
                columns: new[] { "UserId", "ArchivedAtUtc", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Decks_UserId_UpdatedAtUtc_Id",
                table: "Decks",
                columns: new[] { "UserId", "UpdatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewEvents_CardId_ReviewedAtUtc",
                table: "ReviewEvents",
                columns: new[] { "CardId", "ReviewedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewEvents_DeckId_CardId",
                table: "ReviewEvents",
                columns: new[] { "DeckId", "CardId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewEvents_DeckId_ReviewedAtUtc",
                table: "ReviewEvents",
                columns: new[] { "DeckId", "ReviewedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewEvents_UserId_CreatedAtUtc_Id",
                table: "ReviewEvents",
                columns: new[] { "UserId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewEvents_UserId_DeckId",
                table: "ReviewEvents",
                columns: new[] { "UserId", "DeckId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewEvents_UserId_ReviewedAtUtc_Id",
                table: "ReviewEvents",
                columns: new[] { "UserId", "ReviewedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_UpdatedAtUtc",
                table: "Users",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UserSettings_UpdatedAtUtc",
                table: "UserSettings",
                column: "UpdatedAtUtc");

            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER [TR_ReviewEvents_Immutable] ON [ReviewEvents]
                AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50001, ''Review events are immutable.'', 1;
                END')
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER [TR_ReviewEvents_Immutable]");

            migrationBuilder.DropTable(
                name: "CardReviewStates");

            migrationBuilder.DropTable(
                name: "ReviewEvents");

            migrationBuilder.DropTable(
                name: "UserSettings");

            migrationBuilder.DropTable(
                name: "Cards");

            migrationBuilder.DropTable(
                name: "Decks");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
