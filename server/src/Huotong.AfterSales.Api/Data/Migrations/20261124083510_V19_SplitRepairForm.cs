using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Huotong.AfterSales.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class V19_SplitRepairForm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 旧的「维修登记表」拆成两类：返厂工单的归返厂维修登记表，其余归现场维修登记表（没有表结构变化）
            migrationBuilder.Sql(@"UPDATE StoredFiles SET AttachmentType = CASE
                WHEN (SELECT ServiceType FROM WorkOrders WHERE WorkOrders.Id = StoredFiles.OrderId) = 'Factory' THEN 'FactoryRepairForm'
                ELSE 'OnsiteRepairForm' END
                WHERE AttachmentType = 'RepairForm';");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
