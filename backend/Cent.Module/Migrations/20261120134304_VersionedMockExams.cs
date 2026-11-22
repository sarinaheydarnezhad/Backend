using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Cent.Module.Migrations
{
    /// <inheritdoc />
    public partial class VersionedMockExams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cent_syllabus_revisions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RetrievedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_syllabus_revisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cent_exam_content_releases",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ExamVersionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SyllabusRevisionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ManifestJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_exam_content_releases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_exam_content_releases_cent_syllabus_revisions_SyllabusRevisionId",
                        column: x => x.SyllabusRevisionId,
                        principalTable: "cent_syllabus_revisions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_exam_content_releases_cent_users_ReviewedBy",
                        column: x => x.ReviewedBy,
                        principalTable: "cent_users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_exam_versions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConfigurationRevision = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    AvailableForNewStarts = table.Column<bool>(type: "bit", nullable: false),
                    SyllabusRevisionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ContentReleaseId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_exam_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_exam_versions_cent_exam_content_releases_ContentReleaseId",
                        column: x => x.ContentReleaseId,
                        principalTable: "cent_exam_content_releases",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_exam_versions_cent_syllabus_revisions_SyllabusRevisionId",
                        column: x => x.SyllabusRevisionId,
                        principalTable: "cent_syllabus_revisions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_mock_attempts",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExamVersionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConfigurationRevision = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RulesSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ActiveSectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActiveDeadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EffectiveEndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinalizationReason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_mock_attempts", x => x.AttemptId);
                    table.ForeignKey(
                        name: "FK_cent_mock_attempts_cent_attempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "cent_attempts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_mock_attempts_cent_exam_versions_ExamVersionId",
                        column: x => x.ExamVersionId,
                        principalTable: "cent_exam_versions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_attempt_sections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SectionKey = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    QuestionCount = table.Column<int>(type: "int", nullable: false),
                    DurationSeconds = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Deadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_attempt_sections", x => x.Id);
                    table.CheckConstraint("CK_cent_attempt_section_deadline", "([ActivatedAt] IS NULL AND [Deadline] IS NULL AND [State] = 'PENDING') OR ([ActivatedAt] IS NOT NULL AND [Deadline] IS NOT NULL)");
                    table.CheckConstraint("CK_cent_attempt_section_quota", "[QuestionCount] > 0 AND [DurationSeconds] > 0");
                    table.ForeignKey(
                        name: "FK_cent_attempt_sections_cent_mock_attempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "cent_mock_attempts",
                        principalColumn: "AttemptId");
                });

            migrationBuilder.CreateTable(
                name: "cent_exam_command_receipts",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ReceiptJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_exam_command_receipts", x => new { x.AttemptId, x.CommandId });
                    table.ForeignKey(
                        name: "FK_cent_exam_command_receipts_cent_mock_attempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "cent_mock_attempts",
                        principalColumn: "AttemptId");
                });

            migrationBuilder.InsertData(
                table: "cent_exam_versions",
                columns: new[] { "Id", "AvailableForNewStarts", "ConfigurationRevision", "ContentReleaseId", "DefinitionJson", "Revision", "Status", "SyllabusRevisionId" },
                values: new object[,]
                {
                    { "cent-s-2025-2026-r1", false, "blueprint-2025-2026-r1", null, "{\"examVersionId\":\"cent-s-2025-2026-r1\",\"label\":\"CEnT-S 2025\\u20132026\",\"configurationRevision\":\"blueprint-2025-2026-r1\",\"cycleStartDate\":\"2025-11-01\",\"cycleEndDateInclusive\":\"2026-10-31\",\"sections\":[{\"sectionKey\":\"math\",\"order\":1,\"subjectId\":\"math\",\"questionCount\":15,\"durationSeconds\":1800},{\"sectionKey\":\"logic\",\"order\":2,\"subjectId\":\"logic\",\"questionCount\":15,\"durationSeconds\":1800},{\"sectionKey\":\"biology\",\"order\":3,\"subjectId\":\"biology\",\"questionCount\":10,\"durationSeconds\":1200},{\"sectionKey\":\"chemistry\",\"order\":4,\"subjectId\":\"chemistry\",\"questionCount\":10,\"durationSeconds\":1200},{\"sectionKey\":\"physics\",\"order\":5,\"subjectId\":\"physics\",\"questionCount\":5,\"durationSeconds\":600}],\"scoring\":{\"policyId\":\"raw-quarter-point-v1\",\"correctQuarterPoints\":4,\"incorrectQuarterPoints\":-1,\"unansweredQuarterPoints\":0},\"syllabusRevisionId\":null,\"contentReleaseId\":null,\"timingPolicyId\":\"continuous-v1\",\"submissionPolicyId\":null,\"totalQuestions\":55,\"totalDurationSeconds\":6600}", 1, "PUBLISHED", null },
                    { "cent-s-2026-2027-r1", false, "blueprint-2026-2027-r1", null, "{\"examVersionId\":\"cent-s-2026-2027-r1\",\"label\":\"CEnT-S 2026\\u20132027\",\"configurationRevision\":\"blueprint-2026-2027-r1\",\"cycleStartDate\":\"2026-11-01\",\"cycleEndDateInclusive\":\"2027-10-31\",\"sections\":[{\"sectionKey\":\"reading\",\"order\":1,\"subjectId\":\"reading\",\"questionCount\":10,\"durationSeconds\":1800},{\"sectionKey\":\"math\",\"order\":2,\"subjectId\":\"math\",\"questionCount\":15,\"durationSeconds\":1800},{\"sectionKey\":\"logic\",\"order\":3,\"subjectId\":\"logic\",\"questionCount\":15,\"durationSeconds\":1800},{\"sectionKey\":\"biology\",\"order\":4,\"subjectId\":\"biology\",\"questionCount\":10,\"durationSeconds\":1200},{\"sectionKey\":\"chemistry\",\"order\":5,\"subjectId\":\"chemistry\",\"questionCount\":10,\"durationSeconds\":1200},{\"sectionKey\":\"physics\",\"order\":6,\"subjectId\":\"physics\",\"questionCount\":5,\"durationSeconds\":600}],\"scoring\":{\"policyId\":\"raw-quarter-point-v1\",\"correctQuarterPoints\":4,\"incorrectQuarterPoints\":-1,\"unansweredQuarterPoints\":0},\"syllabusRevisionId\":null,\"contentReleaseId\":null,\"timingPolicyId\":\"continuous-v1\",\"submissionPolicyId\":null,\"totalQuestions\":65,\"totalDurationSeconds\":8400}", 1, "PUBLISHED", null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_cent_attempt_sections_active",
                table: "cent_attempt_sections",
                column: "AttemptId",
                unique: true,
                filter: "[State] = 'ACTIVE'");

            migrationBuilder.CreateIndex(
                name: "IX_cent_attempt_sections_AttemptId_Order",
                table: "cent_attempt_sections",
                columns: new[] { "AttemptId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_attempt_sections_AttemptId_SectionKey",
                table: "cent_attempt_sections",
                columns: new[] { "AttemptId", "SectionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_exam_content_releases_ReviewedBy",
                table: "cent_exam_content_releases",
                column: "ReviewedBy");

            migrationBuilder.CreateIndex(
                name: "IX_cent_exam_content_releases_SyllabusRevisionId",
                table: "cent_exam_content_releases",
                column: "SyllabusRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_exam_versions_ConfigurationRevision",
                table: "cent_exam_versions",
                column: "ConfigurationRevision",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_exam_versions_ContentReleaseId",
                table: "cent_exam_versions",
                column: "ContentReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_exam_versions_SyllabusRevisionId",
                table: "cent_exam_versions",
                column: "SyllabusRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_mock_attempts_ExamVersionId",
                table: "cent_mock_attempts",
                column: "ExamVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_mock_attempts_Status_ActiveDeadline",
                table: "cent_mock_attempts",
                columns: new[] { "Status", "ActiveDeadline" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cent_attempt_sections");

            migrationBuilder.DropTable(
                name: "cent_exam_command_receipts");

            migrationBuilder.DropTable(
                name: "cent_mock_attempts");

            migrationBuilder.DropTable(
                name: "cent_exam_versions");

            migrationBuilder.DropTable(
                name: "cent_exam_content_releases");

            migrationBuilder.DropTable(
                name: "cent_syllabus_revisions");
        }
    }
}
