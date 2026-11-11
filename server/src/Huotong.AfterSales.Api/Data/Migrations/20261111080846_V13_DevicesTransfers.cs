using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Huotong.AfterSales.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class V13_DevicesTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceText",
                table: "WorkOrders",
                type: "TEXT",
                maxLength: 250,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PendingTransferToId",
                table: "WorkOrders",
                type: "INTEGER",
                nullable: true);

            // 列默认值手工改为 'Other'：已有文件（含建单照片）全部归「其他附件」。模型上不配置 HasDefaultValue
            migrationBuilder.AddColumn<string>(
                name: "AttachmentType",
                table: "StoredFiles",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Other");

            migrationBuilder.CreateTable(
                name: "OrderDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Seq = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Sn = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderDevices_WorkOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderExits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    EngineerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Via = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ExitedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderExits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderExits_Employees_EngineerId",
                        column: x => x.EngineerId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderExits_WorkOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderTransfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromEngineerId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToEngineerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecidedById = table.Column<int>(type: "INTEGER", nullable: true),
                    Reply = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderTransfers_Employees_DecidedById",
                        column: x => x.DecidedById,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderTransfers_Employees_FromEngineerId",
                        column: x => x.FromEngineerId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderTransfers_Employees_ToEngineerId",
                        column: x => x.ToEngineerId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderTransfers_WorkOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_PendingTransferToId",
                table: "WorkOrders",
                column: "PendingTransferToId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderDevices_OrderId",
                table: "OrderDevices",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderExits_EngineerId",
                table: "OrderExits",
                column: "EngineerId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderExits_OrderId_EngineerId",
                table: "OrderExits",
                columns: new[] { "OrderId", "EngineerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderTransfers_DecidedById",
                table: "OrderTransfers",
                column: "DecidedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrderTransfers_FromEngineerId",
                table: "OrderTransfers",
                column: "FromEngineerId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderTransfers_OrderId",
                table: "OrderTransfers",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderTransfers_ToEngineerId_Status",
                table: "OrderTransfers",
                columns: new[] { "ToEngineerId", "Status" });

            // ---------- 旧数据回填（CHANGES-v1.3 §1.5；迁移只在 SQLite 上执行） ----------

            // 旧单的「型号 + 序列号」变成一台设备（名称为空）
            migrationBuilder.Sql(@"
INSERT INTO OrderDevices (OrderId, Seq, Name, Sn, Model)
SELECT Id, 1, '', trim(DeviceSn), trim(DeviceModel) FROM WorkOrders
WHERE trim(DeviceModel) <> '' OR trim(DeviceSn) <> '';");

            // 设备汇总文字：旧单只有一台设备 → 就是它的型号（没有型号用编号）
            migrationBuilder.Sql(@"
UPDATE WorkOrders SET DeviceText = CASE
  WHEN trim(DeviceModel) <> '' THEN trim(DeviceModel)
  WHEN trim(DeviceSn) <> '' THEN trim(DeviceSn)
  ELSE '未填设备' END;");

            // 旧附件靠列默认值全部落在 Other；ProjectNo / SatDate / SatSigned 保持原样；不回填退出记录
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderDevices");

            migrationBuilder.DropTable(
                name: "OrderExits");

            migrationBuilder.DropTable(
                name: "OrderTransfers");

            migrationBuilder.DropIndex(
                name: "IX_WorkOrders_PendingTransferToId",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "DeviceText",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "PendingTransferToId",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "AttachmentType",
                table: "StoredFiles");
        }
    }
}
