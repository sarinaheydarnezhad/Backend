using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cent.Module.Migrations
{
    /// <inheritdoc />
    public partial class ReaderProtectionBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cent_reader_budgets",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: false),
                    StateJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_reader_budgets", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cent_reader_budgets_UpdatedAt",
                table: "cent_reader_budgets",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cent_reader_budgets");
        }
    }
}
