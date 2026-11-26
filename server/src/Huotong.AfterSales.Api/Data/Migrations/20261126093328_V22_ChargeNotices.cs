using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Huotong.AfterSales.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class V22_ChargeNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MutedNotices",
                table: "Employees",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "OrderCharges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RequesterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SalesId = table.Column<int>(type: "INTEGER", nullable: true),
                    Amount = table.Column<double>(type: "REAL", nullable: true),
                    QuoteNote = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    QuotedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DirectorId = table.Column<int>(type: "INTEGER", nullable: true),
                    Paid = table.Column<bool>(type: "INTEGER", nullable: true),
                    DirectorNote = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SupervisorId = table.Column<int>(type: "INTEGER", nullable: true),
                    SupervisorNote = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FreeById = table.Column<int>(type: "INTEGER", nullable: true),
                    FreeAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReturnNote = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderCharges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderCharges_Employees_DirectorId",
                        column: x => x.DirectorId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderCharges_Employees_FreeById",
                        column: x => x.FreeById,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderCharges_Employees_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderCharges_Employees_SalesId",
                        column: x => x.SalesId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderCharges_Employees_SupervisorId",
                        column: x => x.SupervisorId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderCharges_WorkOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderCharges_DirectorId",
                table: "OrderCharges",
                column: "DirectorId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderCharges_FreeById",
                table: "OrderCharges",
                column: "FreeById");

            migrationBuilder.CreateIndex(
                name: "IX_OrderCharges_OrderId",
                table: "OrderCharges",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderCharges_RequesterId",
                table: "OrderCharges",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderCharges_SalesId",
                table: "OrderCharges",
                column: "SalesId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderCharges_Status",
                table: "OrderCharges",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_OrderCharges_SupervisorId",
                table: "OrderCharges",
                column: "SupervisorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderCharges");

            migrationBuilder.DropColumn(
                name: "MutedNotices",
                table: "Employees");
        }
    }
}
