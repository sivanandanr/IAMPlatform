using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Workflow
{
    /// <inheritdoc />
    public partial class WorkflowReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalDamage",
                schema: "workflow",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DetectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalDamage", x => x.InstanceId);
                    table.ForeignKey(
                        name: "FK_ApprovalDamage_ApprovalInstance_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "workflow",
                        principalTable: "ApprovalInstance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // DL-075b: damage is recorded once and never changed.
            migrationBuilder.Sql(@"CREATE TRIGGER [workflow].[TR_ApprovalDamage_AppendOnly] ON [workflow].[ApprovalDamage]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51309, 'workflow.ApprovalDamage rows are append-only (DL-075).', 1;
END");

            // DL-075h: an UPDATE that matched no rows (a lost optimistic-concurrency race) fires the trigger with both sets
            // empty; it is not a delete, so it must surface as a concurrency conflict, as on PostgreSQL.
            migrationBuilder.Sql(GuardInstance(fixedForEmptyUpdates: true));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GuardInstance(fixedForEmptyUpdates: false));
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [workflow].[TR_ApprovalDamage_AppendOnly];");
            migrationBuilder.DropTable(
                name: "ApprovalDamage",
                schema: "workflow");
        }

        private static string GuardInstance(bool fixedForEmptyUpdates) => @"CREATE OR ALTER TRIGGER [workflow].[TR_ApprovalInstance_Guarded] ON [workflow].[ApprovalInstance]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    " + (fixedForEmptyUpdates
            ? "IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)"
            : "IF NOT EXISTS (SELECT 1 FROM inserted)") + @"
        THROW 51305, 'workflow.ApprovalInstance rows cannot be deleted (DL-072).', 1;
    IF UPDATE([Id]) OR EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
            WHERE i.[RequestItemId] <> d.[RequestItemId] OR i.[Requester] <> d.[Requester] OR i.[Beneficiary] <> d.[Beneficiary])
        THROW 51306, 'An approval instance''s id, request item and people are immutable (DL-072).', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
            WHERE i.[EngineVersion] < d.[EngineVersion] OR i.[ActionCount] <= d.[ActionCount])
        THROW 51307, 'An approval instance only moves forward: every save adds actions (DL-072).', 1;
    IF EXISTS (SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id] WHERE d.[State] IN (N'Approved', N'Rejected', N'Cancelled', N'Withdrawn', N'Returned') AND i.[State] <> d.[State])
        THROW 51308, 'A closed approval instance never changes state (DL-072).', 1;
END";
    }
}
