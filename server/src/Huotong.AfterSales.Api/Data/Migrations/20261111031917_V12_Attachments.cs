using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Huotong.AfterSales.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class V12_Attachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "StoredFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedById",
                table: "StoredFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "StoredFiles",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Restricted",
                table: "StoredFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_CreatedAt",
                table: "StoredFiles",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoredFiles_CreatedAt",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "DeletedById",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "Restricted",
                table: "StoredFiles");
        }
    }
}
