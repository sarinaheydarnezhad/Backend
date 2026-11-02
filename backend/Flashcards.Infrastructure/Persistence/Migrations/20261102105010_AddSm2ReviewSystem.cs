using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flashcards.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSm2ReviewSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SchedulerJson",
                table: "ReviewEvents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewSystem",
                table: "Decks",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: false,
                defaultValue: "leitner");

            migrationBuilder.AddColumn<string>(
                name: "SchedulerStateJson",
                table: "CardReviewStates",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SchedulerJson",
                table: "ReviewEvents");

            migrationBuilder.DropColumn(
                name: "ReviewSystem",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "SchedulerStateJson",
                table: "CardReviewStates");
        }
    }
}
