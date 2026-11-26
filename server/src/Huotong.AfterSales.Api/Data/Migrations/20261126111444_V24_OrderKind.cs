using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Huotong.AfterSales.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class V24_OrderKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "WorkOrders",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Repair");

            migrationBuilder.AddColumn<string>(
                name: "OpenIssues",
                table: "WorkOrders",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_Kind",
                table: "WorkOrders",
                column: "Kind");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkOrders_Kind",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "OpenIssues",
                table: "WorkOrders");
        }
    }
}
