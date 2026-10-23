using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flashcards.Infrastructure.Persistence.Migrations;

public partial class AddGoogleAuthentication : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "GoogleSubject",
            table: "Users",
            type: "varchar(255)",
            unicode: false,
            maxLength: 255,
            nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_Users_GoogleSubject",
            table: "Users",
            column: "GoogleSubject",
            unique: true,
            filter: "[GoogleSubject] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Users_GoogleSubject", table: "Users");
        migrationBuilder.DropColumn(name: "GoogleSubject", table: "Users");
    }
}
