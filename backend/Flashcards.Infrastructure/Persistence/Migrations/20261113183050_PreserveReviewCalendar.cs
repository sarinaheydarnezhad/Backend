using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flashcards.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveReviewCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewCalendarJson",
                table: "ReviewEvents",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuse to erase original calendar evidence during an accidental downgrade.
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [ReviewEvents] WITH (TABLOCKX, HOLDLOCK) WHERE [ReviewCalendarJson] IS NOT NULL) THROW 51000, 'Review calendar evidence exists. Retain the column and use a forward-compatible rollback.', 1;");
            migrationBuilder.DropColumn(
                name: "ReviewCalendarJson",
                table: "ReviewEvents");
        }
    }
}
