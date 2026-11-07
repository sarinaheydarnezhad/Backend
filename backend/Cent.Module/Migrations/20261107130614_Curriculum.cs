using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cent.Module.Migrations
{
    /// <inheritdoc />
    public partial class Curriculum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Topic",
                table: "cent_questions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(120)",
                oldMaxLength: 120);

            migrationBuilder.AddColumn<string>(
                name: "FigureSvg",
                table: "cent_questions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportKey",
                table: "cent_questions",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "cent_questions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceExerciseId",
                table: "cent_questions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceSectionId",
                table: "cent_questions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TopicId",
                table: "cent_questions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceId",
                table: "cent_documents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssessmentMode",
                table: "cent_attempts",
                type: "nvarchar(36)",
                maxLength: 36,
                nullable: false,
                defaultValue: "overview");

            migrationBuilder.AddColumn<string>(
                name: "CourseCodesJson",
                table: "cent_attempts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "DurationMinutes",
                table: "cent_attempts",
                type: "int",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.CreateTable(
                name: "cent_courses",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EnglishTitle = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    TrainingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Active = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_courses", x => x.Code);
                    table.ForeignKey(
                        name: "FK_cent_courses_cent_trainings_TrainingId",
                        column: x => x.TrainingId,
                        principalTable: "cent_trainings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_curriculum_imports",
                columns: table => new
                {
                    PackageHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourceCount = table.Column<int>(type: "int", nullable: false),
                    TopicCount = table.Column<int>(type: "int", nullable: false),
                    QuestionCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_curriculum_imports", x => x.PackageHash);
                });

            migrationBuilder.CreateTable(
                name: "cent_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImportKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CourseCode = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OriginalTitle = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    OriginalHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PlainText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_sources_cent_courses_CourseCode",
                        column: x => x.CourseCode,
                        principalTable: "cent_courses",
                        principalColumn: "Code");
                });

            migrationBuilder.CreateTable(
                name: "cent_chapters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImportKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CourseCode = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_chapters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_chapters_cent_courses_CourseCode",
                        column: x => x.CourseCode,
                        principalTable: "cent_courses",
                        principalColumn: "Code");
                    table.ForeignKey(
                        name: "FK_cent_chapters_cent_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "cent_sources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_topics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImportKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ChapterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CourseCode = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    EnglishTitle = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Foundation = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_topics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_topics_cent_chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "cent_chapters",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_topics_cent_courses_CourseCode",
                        column: x => x.CourseCode,
                        principalTable: "cent_courses",
                        principalColumn: "Code");
                });

            migrationBuilder.CreateTable(
                name: "cent_source_sections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TopicId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImportKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Heading = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Html = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_source_sections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_source_sections_cent_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "cent_sources",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_source_sections_cent_topics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "cent_topics",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_source_exercises",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TopicId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImportKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OriginalCorrectAnswer = table.Column<int>(type: "int", nullable: true),
                    Solution = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReviewNote = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_source_exercises", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_source_exercises_cent_source_sections_SourceSectionId",
                        column: x => x.SourceSectionId,
                        principalTable: "cent_source_sections",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_source_exercises_cent_topics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "cent_topics",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_cent_questions_ImportKey",
                table: "cent_questions",
                column: "ImportKey",
                unique: true,
                filter: "[ImportKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_cent_questions_SourceExerciseId",
                table: "cent_questions",
                column: "SourceExerciseId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_questions_SourceSectionId",
                table: "cent_questions",
                column: "SourceSectionId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_questions_Subject_Active_TopicId",
                table: "cent_questions",
                columns: new[] { "Subject", "Active", "TopicId" });

            migrationBuilder.CreateIndex(
                name: "IX_cent_questions_TopicId",
                table: "cent_questions",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_documents_SourceId",
                table: "cent_documents",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_chapters_CourseCode",
                table: "cent_chapters",
                column: "CourseCode");

            migrationBuilder.CreateIndex(
                name: "IX_cent_chapters_ImportKey",
                table: "cent_chapters",
                column: "ImportKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_chapters_SourceId",
                table: "cent_chapters",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_courses_TrainingId",
                table: "cent_courses",
                column: "TrainingId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_source_exercises_ImportKey",
                table: "cent_source_exercises",
                column: "ImportKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_source_exercises_SourceSectionId",
                table: "cent_source_exercises",
                column: "SourceSectionId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_source_exercises_TopicId",
                table: "cent_source_exercises",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_source_sections_ImportKey",
                table: "cent_source_sections",
                column: "ImportKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_source_sections_SourceId_SortOrder",
                table: "cent_source_sections",
                columns: new[] { "SourceId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_source_sections_TopicId",
                table: "cent_source_sections",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_sources_CourseCode",
                table: "cent_sources",
                column: "CourseCode");

            migrationBuilder.CreateIndex(
                name: "IX_cent_sources_ImportKey",
                table: "cent_sources",
                column: "ImportKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_topics_ChapterId",
                table: "cent_topics",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_topics_CourseCode_ChapterId_SortOrder",
                table: "cent_topics",
                columns: new[] { "CourseCode", "ChapterId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_cent_topics_ImportKey",
                table: "cent_topics",
                column: "ImportKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_cent_documents_cent_sources_SourceId",
                table: "cent_documents",
                column: "SourceId",
                principalTable: "cent_sources",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_cent_questions_cent_source_exercises_SourceExerciseId",
                table: "cent_questions",
                column: "SourceExerciseId",
                principalTable: "cent_source_exercises",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_cent_questions_cent_source_sections_SourceSectionId",
                table: "cent_questions",
                column: "SourceSectionId",
                principalTable: "cent_source_sections",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_cent_questions_cent_topics_TopicId",
                table: "cent_questions",
                column: "TopicId",
                principalTable: "cent_topics",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cent_documents_cent_sources_SourceId",
                table: "cent_documents");

            migrationBuilder.DropForeignKey(
                name: "FK_cent_questions_cent_source_exercises_SourceExerciseId",
                table: "cent_questions");

            migrationBuilder.DropForeignKey(
                name: "FK_cent_questions_cent_source_sections_SourceSectionId",
                table: "cent_questions");

            migrationBuilder.DropForeignKey(
                name: "FK_cent_questions_cent_topics_TopicId",
                table: "cent_questions");

            migrationBuilder.DropTable(
                name: "cent_curriculum_imports");

            migrationBuilder.DropTable(
                name: "cent_source_exercises");

            migrationBuilder.DropTable(
                name: "cent_source_sections");

            migrationBuilder.DropTable(
                name: "cent_topics");

            migrationBuilder.DropTable(
                name: "cent_chapters");

            migrationBuilder.DropTable(
                name: "cent_sources");

            migrationBuilder.DropTable(
                name: "cent_courses");

            migrationBuilder.DropIndex(
                name: "IX_cent_questions_ImportKey",
                table: "cent_questions");

            migrationBuilder.DropIndex(
                name: "IX_cent_questions_SourceExerciseId",
                table: "cent_questions");

            migrationBuilder.DropIndex(
                name: "IX_cent_questions_SourceSectionId",
                table: "cent_questions");

            migrationBuilder.DropIndex(
                name: "IX_cent_questions_Subject_Active_TopicId",
                table: "cent_questions");

            migrationBuilder.DropIndex(
                name: "IX_cent_questions_TopicId",
                table: "cent_questions");

            migrationBuilder.DropIndex(
                name: "IX_cent_documents_SourceId",
                table: "cent_documents");

            migrationBuilder.DropColumn(
                name: "FigureSvg",
                table: "cent_questions");

            migrationBuilder.DropColumn(
                name: "ImportKey",
                table: "cent_questions");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "cent_questions");

            migrationBuilder.DropColumn(
                name: "SourceExerciseId",
                table: "cent_questions");

            migrationBuilder.DropColumn(
                name: "SourceSectionId",
                table: "cent_questions");

            migrationBuilder.DropColumn(
                name: "TopicId",
                table: "cent_questions");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "cent_documents");

            migrationBuilder.DropColumn(
                name: "AssessmentMode",
                table: "cent_attempts");

            migrationBuilder.DropColumn(
                name: "CourseCodesJson",
                table: "cent_attempts");

            migrationBuilder.DropColumn(
                name: "DurationMinutes",
                table: "cent_attempts");

            migrationBuilder.AlterColumn<string>(
                name: "Topic",
                table: "cent_questions",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500);
        }
    }
}
