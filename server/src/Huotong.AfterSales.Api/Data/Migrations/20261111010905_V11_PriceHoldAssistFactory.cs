using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Huotong.AfterSales.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class V11_PriceHoldAssistFactory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "HoldAt",
                table: "WorkOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HoldById",
                table: "WorkOrders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HoldFromStatus",
                table: "WorkOrders",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HoldReasonType",
                table: "WorkOrders",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PriceSet",
                table: "WorkOrders",
                type: "INTEGER",
                nullable: false,
                // 手工改为 true：已有工单全部视为已定价（新建工单由代码显式写 false）
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PriceSetAt",
                table: "WorkOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "StatusLogs",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FactoryRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Node = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OperatorId = table.Column<int>(type: "INTEGER", nullable: false),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Courier = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    TrackingNo = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FactoryRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FactoryRecords_Employees_OperatorId",
                        column: x => x.OperatorId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FactoryRecords_WorkOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderAssistants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    EngineerId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedById = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderAssistants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderAssistants_Employees_AddedById",
                        column: x => x.AddedById,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderAssistants_Employees_EngineerId",
                        column: x => x.EngineerId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderAssistants_WorkOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RequesterId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HoldReasonType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    TargetEngineerId = table.Column<int>(type: "INTEGER", nullable: true),
                    DeciderId = table.Column<int>(type: "INTEGER", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Reply = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderRequests_Employees_DeciderId",
                        column: x => x.DeciderId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderRequests_Employees_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderRequests_Employees_TargetEngineerId",
                        column: x => x.TargetEngineerId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderRequests_WorkOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceSuggestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    SalesId = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<double>(type: "REAL", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceSuggestions_Employees_SalesId",
                        column: x => x.SalesId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceSuggestions_WorkOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FactoryRecords_OperatorId",
                table: "FactoryRecords",
                column: "OperatorId");

            migrationBuilder.CreateIndex(
                name: "IX_FactoryRecords_OrderId_Node",
                table: "FactoryRecords",
                columns: new[] { "OrderId", "Node" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderAssistants_AddedById",
                table: "OrderAssistants",
                column: "AddedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrderAssistants_EngineerId",
                table: "OrderAssistants",
                column: "EngineerId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderAssistants_OrderId_EngineerId",
                table: "OrderAssistants",
                columns: new[] { "OrderId", "EngineerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderRequests_DeciderId",
                table: "OrderRequests",
                column: "DeciderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderRequests_OrderId",
                table: "OrderRequests",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderRequests_RequesterId",
                table: "OrderRequests",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderRequests_Status",
                table: "OrderRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_OrderRequests_TargetEngineerId",
                table: "OrderRequests",
                column: "TargetEngineerId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceSuggestions_OrderId",
                table: "PriceSuggestions",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceSuggestions_SalesId",
                table: "PriceSuggestions",
                column: "SalesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FactoryRecords");

            migrationBuilder.DropTable(
                name: "OrderAssistants");

            migrationBuilder.DropTable(
                name: "OrderRequests");

            migrationBuilder.DropTable(
                name: "PriceSuggestions");

            migrationBuilder.DropColumn(
                name: "HoldAt",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "HoldById",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "HoldFromStatus",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "HoldReasonType",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "PriceSet",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "PriceSetAt",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "StatusLogs");
        }
    }
}
