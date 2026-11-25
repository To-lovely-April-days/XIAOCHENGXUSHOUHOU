using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Huotong.AfterSales.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class V20_ServiceForms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerConfirm",
                table: "WorkOrders",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TestResult",
                table: "WorkOrders",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoStage",
                table: "StoredFiles",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetServiceType",
                table: "OrderRequests",
                type: "TEXT",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomerConfirm",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "TestResult",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "PhotoStage",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "TargetServiceType",
                table: "OrderRequests");
        }
    }
}
