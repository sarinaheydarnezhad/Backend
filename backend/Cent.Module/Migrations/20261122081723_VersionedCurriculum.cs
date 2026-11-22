using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cent.Module.Migrations
{
    /// <inheritdoc />
    public partial class VersionedCurriculum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cent_curriculum_revisions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SyllabusRevisionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PackageHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_curriculum_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_curriculum_revisions_cent_syllabus_revisions_SyllabusRevisionId",
                        column: x => x.SyllabusRevisionId,
                        principalTable: "cent_syllabus_revisions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_practice_content",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReleaseId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PassagesJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_practice_content", x => x.AttemptId);
                    table.ForeignKey(
                        name: "FK_cent_practice_content_cent_attempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "cent_attempts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_practice_content_cent_curriculum_revisions_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "cent_curriculum_revisions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_cent_curriculum_revisions_PackageHash",
                table: "cent_curriculum_revisions",
                column: "PackageHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_curriculum_revisions_SyllabusRevisionId",
                table: "cent_curriculum_revisions",
                column: "SyllabusRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_practice_content_ReleaseId",
                table: "cent_practice_content",
                column: "ReleaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cent_practice_content");

            migrationBuilder.DropTable(
                name: "cent_curriculum_revisions");
        }
    }
}
