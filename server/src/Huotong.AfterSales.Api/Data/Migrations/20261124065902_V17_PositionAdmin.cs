using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Huotong.AfterSales.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class V17_PositionAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "Employees",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Position",
                table: "Employees",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Engineer");

            // 旧数据：岗位 = 原角色；原来的售后主管都能登录电脑后台，所以全部带上管理员权限，升级后没人被锁在外面
            migrationBuilder.Sql("UPDATE Employees SET Position = Role;");
            migrationBuilder.Sql("UPDATE Employees SET IsAdmin = 1 WHERE Role = 'Supervisor';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "Employees");
        }
    }
}
