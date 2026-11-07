using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cent.Module.Migrations
{
    /// <inheritdoc />
    public partial class InitialCent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cent_audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cent_questions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Topic = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Difficulty = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    CorrectAnswer = table.Column<int>(type: "int", nullable: false),
                    Explanation = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_questions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cent_trainings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    IncludedInSubscription = table.Column<bool>(type: "bit", nullable: false),
                    Published = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_trainings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cent_users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PurchaseVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cent_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TrainingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    PageCount = table.Column<int>(type: "int", nullable: false),
                    Published = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_documents_cent_trainings_TrainingId",
                        column: x => x.TrainingId,
                        principalTable: "cent_trainings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TrainingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PriceRial = table.Column<long>(type: "bigint", nullable: false),
                    DurationDays = table.Column<int>(type: "int", nullable: true),
                    Active = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_products", x => x.Id);
                    table.CheckConstraint("CK_cent_product_access", "([Kind] = 'TRAINING' AND [TrainingId] IS NOT NULL AND [DurationDays] IS NULL) OR ([Kind] = 'SUBSCRIPTION' AND [TrainingId] IS NULL AND [DurationDays] BETWEEN 1 AND 366)");
                    table.CheckConstraint("CK_cent_product_price", "[PriceRial] > 0");
                    table.ForeignKey(
                        name: "FK_cent_products_cent_trainings_TrainingId",
                        column: x => x.TrainingId,
                        principalTable: "cent_trainings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_attempts_cent_users_UserId",
                        column: x => x.UserId,
                        principalTable: "cent_users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_sessions_cent_users_UserId",
                        column: x => x.UserId,
                        principalTable: "cent_users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_document_pages",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Image = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_document_pages", x => new { x.DocumentId, x.Number });
                    table.ForeignKey(
                        name: "FK_cent_document_pages_cent_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "cent_documents",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_document_sections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartPage = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_document_sections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_document_sections_cent_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "cent_documents",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Page = table.Column<int>(type: "int", nullable: false),
                    AnchorY = table.Column<double>(type: "float", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_notes_cent_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "cent_documents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_notes_cent_users_UserId",
                        column: x => x.UserId,
                        principalTable: "cent_users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_study_progress",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastPage = table.Column<int>(type: "int", nullable: false),
                    FurthestPage = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_study_progress", x => new { x.UserId, x.DocumentId });
                    table.ForeignKey(
                        name: "FK_cent_study_progress_cent_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "cent_documents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_study_progress_cent_users_UserId",
                        column: x => x.UserId,
                        principalTable: "cent_users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TrainingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DurationDays = table.Column<int>(type: "int", nullable: true),
                    AmountRial = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Authority = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    Reference = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_orders_cent_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "cent_products",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_orders_cent_trainings_TrainingId",
                        column: x => x.TrainingId,
                        principalTable: "cent_trainings",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_orders_cent_users_UserId",
                        column: x => x.UserId,
                        principalTable: "cent_users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_ai_analyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_ai_analyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_ai_analyses_cent_attempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "cent_attempts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_attempt_questions",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Answer = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_attempt_questions", x => new { x.AttemptId, x.Position });
                    table.ForeignKey(
                        name: "FK_cent_attempt_questions_cent_attempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "cent_attempts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_entitlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TrainingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_entitlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_entitlements_cent_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "cent_orders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_entitlements_cent_trainings_TrainingId",
                        column: x => x.TrainingId,
                        principalTable: "cent_trainings",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_entitlements_cent_users_UserId",
                        column: x => x.UserId,
                        principalTable: "cent_users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_payment_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Code = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_payment_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_payment_events_cent_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "cent_orders",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_cent_ai_analyses_AttemptId",
                table: "cent_ai_analyses",
                column: "AttemptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_attempts_UserId_RequestId",
                table: "cent_attempts",
                columns: new[] { "UserId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_audit_events_CreatedAt",
                table: "cent_audit_events",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_cent_document_sections_DocumentId_StartPage",
                table: "cent_document_sections",
                columns: new[] { "DocumentId", "StartPage" });

            migrationBuilder.CreateIndex(
                name: "IX_cent_documents_TrainingId_SortOrder",
                table: "cent_documents",
                columns: new[] { "TrainingId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_cent_entitlements_OrderId",
                table: "cent_entitlements",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_entitlements_TrainingId",
                table: "cent_entitlements",
                column: "TrainingId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_entitlements_UserId_Kind_TrainingId",
                table: "cent_entitlements",
                columns: new[] { "UserId", "Kind", "TrainingId" });

            migrationBuilder.CreateIndex(
                name: "IX_cent_notes_DocumentId",
                table: "cent_notes",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_notes_UserId_DocumentId_Page",
                table: "cent_notes",
                columns: new[] { "UserId", "DocumentId", "Page" });

            migrationBuilder.CreateIndex(
                name: "IX_cent_orders_Authority",
                table: "cent_orders",
                column: "Authority",
                unique: true,
                filter: "[Authority] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_cent_orders_ProductId",
                table: "cent_orders",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_orders_Provider_Reference",
                table: "cent_orders",
                columns: new[] { "Provider", "Reference" },
                unique: true,
                filter: "[Reference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_cent_orders_TrainingId",
                table: "cent_orders",
                column: "TrainingId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_orders_UserId_RequestId",
                table: "cent_orders",
                columns: new[] { "UserId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_payment_events_OrderId_CreatedAt",
                table: "cent_payment_events",
                columns: new[] { "OrderId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_cent_products_TrainingId",
                table: "cent_products",
                column: "TrainingId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_questions_Active_Kind",
                table: "cent_questions",
                columns: new[] { "Active", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_cent_sessions_TokenHash",
                table: "cent_sessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_sessions_UserId_ExpiresAt",
                table: "cent_sessions",
                columns: new[] { "UserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_cent_study_progress_DocumentId",
                table: "cent_study_progress",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_users_NormalizedEmail",
                table: "cent_users",
                column: "NormalizedEmail",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cent_ai_analyses");

            migrationBuilder.DropTable(
                name: "cent_attempt_questions");

            migrationBuilder.DropTable(
                name: "cent_audit_events");

            migrationBuilder.DropTable(
                name: "cent_document_pages");

            migrationBuilder.DropTable(
                name: "cent_document_sections");

            migrationBuilder.DropTable(
                name: "cent_entitlements");

            migrationBuilder.DropTable(
                name: "cent_notes");

            migrationBuilder.DropTable(
                name: "cent_payment_events");

            migrationBuilder.DropTable(
                name: "cent_questions");

            migrationBuilder.DropTable(
                name: "cent_sessions");

            migrationBuilder.DropTable(
                name: "cent_study_progress");

            migrationBuilder.DropTable(
                name: "cent_attempts");

            migrationBuilder.DropTable(
                name: "cent_orders");

            migrationBuilder.DropTable(
                name: "cent_documents");

            migrationBuilder.DropTable(
                name: "cent_products");

            migrationBuilder.DropTable(
                name: "cent_users");

            migrationBuilder.DropTable(
                name: "cent_trainings");
        }
    }
}
