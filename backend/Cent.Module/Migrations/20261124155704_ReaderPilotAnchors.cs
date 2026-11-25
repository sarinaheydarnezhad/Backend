using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cent.Module.Migrations
{
    /// <inheritdoc />
    public partial class ReaderPilotAnchors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnchorJson",
                table: "cent_notes",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnchorJson",
                table: "cent_notes");
        }
    }
}
