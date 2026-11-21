using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Workflow
{
    /// <inheritdoc />
    public partial class WorkflowCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                schema: "workflow",
                table: "ApprovalInstance",
                type: "nchar(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ApprovalInstance_CorrelationId_Code",
                schema: "workflow",
                table: "ApprovalInstance",
                sql: "DATALENGTH([CorrelationId]) > 0 AND DATALENGTH([CorrelationId]) = DATALENGTH(LTRIM(RTRIM([CorrelationId])))");
            // DL-094, hand-written and reviewed (not generated). The correlation id is a W3C trace id, set when the approval starts and
            // never changed afterwards (a separate trigger, so the existing guard stays as reviewed).
            migrationBuilder.Sql(@"ALTER TABLE [workflow].[ApprovalInstance] ADD CONSTRAINT [CK_ApprovalInstance_CorrelationId] CHECK ([CorrelationId] IS NULL OR (LEN([CorrelationId]) = 32 AND [CorrelationId] NOT LIKE '%[^0-9a-f]%'));");
            migrationBuilder.Sql(@"CREATE TRIGGER [workflow].[TR_ApprovalInstance_Correlation] ON [workflow].[ApprovalInstance]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
            WHERE NOT EXISTS (SELECT i.[CorrelationId] INTERSECT SELECT d.[CorrelationId]))
        THROW 51309, 'An approval instance''s correlation id never changes (DL-094).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [workflow].[TR_ApprovalInstance_Correlation];");
            migrationBuilder.Sql(@"ALTER TABLE [workflow].[ApprovalInstance] DROP CONSTRAINT [CK_ApprovalInstance_CorrelationId];");
            migrationBuilder.DropCheckConstraint(
                name: "CK_ApprovalInstance_CorrelationId_Code",
                schema: "workflow",
                table: "ApprovalInstance");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                schema: "workflow",
                table: "ApprovalInstance");
        }
    }
}
