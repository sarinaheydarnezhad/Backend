using Microsoft.EntityFrameworkCore.Migrations;

namespace Flashcards.Infrastructure.Persistence.Migrations;

public partial class AddPhoneAuthentication : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "PhoneNumber", table: "Users", type: "varchar(16)",
            unicode: false, maxLength: 16, nullable: true);
        migrationBuilder.CreateIndex(name: "IX_Users_PhoneNumber", table: "Users", column: "PhoneNumber",
            unique: true, filter: "[PhoneNumber] IS NOT NULL");
        migrationBuilder.CreateTable(name: "OtpChallenges", columns: table => new
        {
            PhoneNumber = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
            Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            Purpose = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
            CodeHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
            ProofHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
            RequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
            WindowStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
            ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
            VerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
            ProofExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
            ConsumedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
            Attempts = table.Column<int>(type: "int", nullable: false),
            RequestsInWindow = table.Column<int>(type: "int", nullable: false),
            Delivered = table.Column<bool>(type: "bit", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_OtpChallenges", item => item.PhoneNumber));
        migrationBuilder.CreateIndex(name: "IX_OtpChallenges_Id", table: "OtpChallenges", column: "Id", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("OtpChallenges");
        migrationBuilder.DropIndex("IX_Users_PhoneNumber", "Users");
        migrationBuilder.DropColumn("PhoneNumber", "Users");
    }
}
