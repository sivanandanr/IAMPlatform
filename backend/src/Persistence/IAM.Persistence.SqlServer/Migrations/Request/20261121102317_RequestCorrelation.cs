using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Request
{
    /// <inheritdoc />
    public partial class RequestCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                schema: "request",
                table: "RequestItem",
                type: "nchar(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RequestItem_CorrelationId_Code",
                schema: "request",
                table: "RequestItem",
                sql: "DATALENGTH([CorrelationId]) > 0 AND DATALENGTH([CorrelationId]) = DATALENGTH(LTRIM(RTRIM([CorrelationId])))");
            // DL-094, hand-written and reviewed (not generated). The correlation id is a W3C trace id: 32 lower-case hex digits.
            migrationBuilder.Sql(@"ALTER TABLE [request].[RequestItem] ADD CONSTRAINT [CK_RequestItem_CorrelationId] CHECK ([CorrelationId] IS NULL OR (LEN([CorrelationId]) = 32 AND [CorrelationId] NOT LIKE '%[^0-9a-f]%'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE [request].[RequestItem] DROP CONSTRAINT [CK_RequestItem_CorrelationId];");
            migrationBuilder.DropCheckConstraint(
                name: "CK_RequestItem_CorrelationId_Code",
                schema: "request",
                table: "RequestItem");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                schema: "request",
                table: "RequestItem");
        }
    }
}
