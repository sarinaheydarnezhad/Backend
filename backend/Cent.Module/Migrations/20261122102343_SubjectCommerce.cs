using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cent.Module.Migrations
{
    /// <inheritdoc />
    public partial class SubjectCommerce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_cent_product_access",
                table: "cent_products");

            migrationBuilder.CreateTable(
                name: "cent_recommended_bundles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SubjectsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Rationale = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_recommended_bundles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cent_subject_access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntitlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ContentReleaseId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_subject_access", x => x.Id);
                    table.CheckConstraint("CK_cent_subject_term", "[ExpiresAt] > [StartsAt]");
                    table.ForeignKey(
                        name: "FK_cent_subject_access_cent_courses_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "cent_courses",
                        principalColumn: "Code");
                    table.ForeignKey(
                        name: "FK_cent_subject_access_cent_entitlements_EntitlementId",
                        column: x => x.EntitlementId,
                        principalTable: "cent_entitlements",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_subject_access_cent_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "cent_orders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_subject_access_cent_users_UserId",
                        column: x => x.UserId,
                        principalTable: "cent_users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_subject_offers",
                columns: table => new
                {
                    SubjectId = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentReleaseId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ReviewedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Available = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_subject_offers", x => x.SubjectId);
                    table.ForeignKey(
                        name: "FK_cent_subject_offers_cent_courses_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "cent_courses",
                        principalColumn: "Code");
                    table.ForeignKey(
                        name: "FK_cent_subject_offers_cent_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "cent_products",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_subject_order_lines",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentReleaseId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ListRial = table.Column<long>(type: "bigint", nullable: false),
                    DiscountRial = table.Column<long>(type: "bigint", nullable: false),
                    PayableRial = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_subject_order_lines", x => new { x.OrderId, x.SubjectId });
                    table.CheckConstraint("CK_cent_subject_line_amount", "[ListRial] > 0 AND [DiscountRial] >= 0 AND [PayableRial] = [ListRial] - [DiscountRial] AND [PayableRial] > 0");
                    table.ForeignKey(
                        name: "FK_cent_subject_order_lines_cent_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "cent_orders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_subject_order_lines_cent_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "cent_products",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_subject_quotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_subject_quotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cent_subject_quotes_cent_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "cent_orders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_subject_quotes_cent_users_UserId",
                        column: x => x.UserId,
                        principalTable: "cent_users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "cent_subject_orders",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InitiationState = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    DevelopmentOutcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cent_subject_orders", x => x.OrderId);
                    table.ForeignKey(
                        name: "FK_cent_subject_orders_cent_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "cent_orders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_cent_subject_orders_cent_subject_quotes_QuoteId",
                        column: x => x.QuoteId,
                        principalTable: "cent_subject_quotes",
                        principalColumn: "Id");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_cent_product_access",
                table: "cent_products",
                sql: "([Kind] IN ('TRAINING', 'SUBJECT') AND [TrainingId] IS NOT NULL AND [DurationDays] IS NULL) OR ([Kind] = 'SUBSCRIPTION' AND [TrainingId] IS NULL AND [DurationDays] BETWEEN 1 AND 366)");

            migrationBuilder.CreateIndex(
                name: "IX_cent_subject_access_EntitlementId",
                table: "cent_subject_access",
                column: "EntitlementId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_subject_access_OrderId_SubjectId",
                table: "cent_subject_access",
                columns: new[] { "OrderId", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_subject_access_SubjectId",
                table: "cent_subject_access",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_subject_access_UserId_SubjectId_ExpiresAt",
                table: "cent_subject_access",
                columns: new[] { "UserId", "SubjectId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_cent_subject_offers_ProductId",
                table: "cent_subject_offers",
                column: "ProductId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_subject_order_lines_ProductId",
                table: "cent_subject_order_lines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_subject_orders_QuoteId",
                table: "cent_subject_orders",
                column: "QuoteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cent_subject_quotes_OrderId",
                table: "cent_subject_quotes",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_cent_subject_quotes_UserId_ExpiresAt",
                table: "cent_subject_quotes",
                columns: new[] { "UserId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Subject commerce contains financial evidence. Disable new checkout and retain orders, grants and compatible payment readers; use a separately reviewed recovery plan.");
        }
    }
}
