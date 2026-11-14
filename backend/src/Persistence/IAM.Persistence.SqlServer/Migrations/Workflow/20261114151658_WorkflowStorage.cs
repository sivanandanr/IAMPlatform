using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Workflow
{
    /// <inheritdoc />
    public partial class WorkflowStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "workflow");

            migrationBuilder.CreateTable(
                name: "ApprovalInstance",
                schema: "workflow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Requester = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Beneficiary = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    EngineVersion = table.Column<long>(type: "bigint", nullable: false),
                    ActionCount = table.Column<int>(type: "int", nullable: false),
                    ProvisioningAuthorized = table.Column<bool>(type: "bit", nullable: false),
                    NextDeadlineUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalInstance", x => x.Id);
                    table.CheckConstraint("CK_ApprovalInstance_Beneficiary_Code", "DATALENGTH([Beneficiary]) > 0 AND DATALENGTH([Beneficiary]) = DATALENGTH(LTRIM(RTRIM([Beneficiary])))");
                    table.CheckConstraint("CK_ApprovalInstance_Requester_Code", "DATALENGTH([Requester]) > 0 AND DATALENGTH([Requester]) = DATALENGTH(LTRIM(RTRIM([Requester])))");
                    table.CheckConstraint("CK_ApprovalInstance_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                });

            migrationBuilder.CreateTable(
                name: "ApprovalAction",
                schema: "workflow",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Payload = table.Column<string>(type: "nvarchar(max)", maxLength: 4000000, nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2"),
                    VersionAfter = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalAction", x => new { x.InstanceId, x.Sequence });
                    table.CheckConstraint("CK_ApprovalAction_Actor_Code", "DATALENGTH([Actor]) > 0 AND DATALENGTH([Actor]) = DATALENGTH(LTRIM(RTRIM([Actor])))");
                    table.CheckConstraint("CK_ApprovalAction_Kind_Code", "DATALENGTH([Kind]) > 0 AND DATALENGTH([Kind]) = DATALENGTH(LTRIM(RTRIM([Kind])))");
                    table.ForeignKey(
                        name: "FK_ApprovalAction_ApprovalInstance_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "workflow",
                        principalTable: "ApprovalInstance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalHistory",
                schema: "workflow",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventVersion = table.Column<long>(type: "bigint", nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false, collation: "Latin1_General_100_BIN2"),
                    StageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalHistory", x => new { x.InstanceId, x.EventVersion });
                    table.CheckConstraint("CK_ApprovalHistory_Actor_Code", "DATALENGTH([Actor]) > 0 AND DATALENGTH([Actor]) = DATALENGTH(LTRIM(RTRIM([Actor])))");
                    table.CheckConstraint("CK_ApprovalHistory_Kind_Code", "DATALENGTH([Kind]) > 0 AND DATALENGTH([Kind]) = DATALENGTH(LTRIM(RTRIM([Kind])))");
                    table.ForeignKey(
                        name: "FK_ApprovalHistory_ApprovalInstance_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "workflow",
                        principalTable: "ApprovalInstance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalInstance_RequestItemId",
                schema: "workflow",
                table: "ApprovalInstance",
                column: "RequestItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalInstance_State_NextDeadlineUtc",
                schema: "workflow",
                table: "ApprovalInstance",
                columns: new[] { "State", "NextDeadlineUtc" });

            // Guards (DL-072a): append-only logs with no gaps, a summary that only moves forward, closed instances final.
            migrationBuilder.Sql(@"ALTER TABLE [workflow].[ApprovalInstance] ADD CONSTRAINT [CK_ApprovalInstance_State] CHECK ([State] IN (N'InProgress', N'Held', N'Approved', N'Rejected', N'Cancelled', N'Withdrawn', N'Returned'));");
            migrationBuilder.Sql(@"ALTER TABLE [workflow].[ApprovalInstance] ADD CONSTRAINT [CK_ApprovalInstance_Counts] CHECK ([EngineVersion] >= 1 AND [ActionCount] >= 1);");
            migrationBuilder.Sql(@"ALTER TABLE [workflow].[ApprovalAction] ADD CONSTRAINT [CK_ApprovalAction_Kind] CHECK ([Kind] IN (N'Bound', N'ApproversSupplied', N'Decided', N'Completed', N'Returned', N'Withdrawn', N'Cancelled', N'Escalated', N'EscalationProposed', N'EscalationApproved', N'EscalationWithdrawn', N'TimeoutsApplied'));");
            migrationBuilder.Sql(@"ALTER TABLE [workflow].[ApprovalAction] ADD CONSTRAINT [CK_ApprovalAction_Numbers] CHECK ([Sequence] >= 1 AND [VersionAfter] >= 1 AND LEN([Payload]) > 0);");
            migrationBuilder.Sql(@"ALTER TABLE [workflow].[ApprovalAction] ADD CONSTRAINT [CK_ApprovalAction_FirstIsBound] CHECK (([Sequence] = 1 AND [Kind] = N'Bound') OR ([Sequence] > 1 AND [Kind] <> N'Bound'));");
            migrationBuilder.Sql(@"ALTER TABLE [workflow].[ApprovalHistory] ADD CONSTRAINT [CK_ApprovalHistory_Version] CHECK ([EventVersion] >= 1);");
            migrationBuilder.Sql(@"CREATE TRIGGER [workflow].[TR_ApprovalAction_AppendOnly] ON [workflow].[ApprovalAction]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51301, 'workflow.ApprovalAction rows are append-only (DL-072).', 1;
    IF EXISTS (SELECT 1 FROM inserted i WHERE i.[Sequence] > 1 AND NOT EXISTS (
            SELECT 1 FROM [workflow].[ApprovalAction] a WITH (READCOMMITTEDLOCK)
            WHERE a.[InstanceId] = i.[InstanceId] AND a.[Sequence] = i.[Sequence] - 1 AND a.[VersionAfter] <= i.[VersionAfter]))
        THROW 51302, 'Approval actions are numbered 1, 2, 3 with no gaps, and versions never go back (DL-072).', 1;
END");
            migrationBuilder.Sql(@"CREATE TRIGGER [workflow].[TR_ApprovalHistory_AppendOnly] ON [workflow].[ApprovalHistory]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51303, 'workflow.ApprovalHistory rows are append-only (DL-072).', 1;
    IF EXISTS (SELECT 1 FROM inserted i WHERE i.[EventVersion] > 1 AND NOT EXISTS (
            SELECT 1 FROM [workflow].[ApprovalHistory] h WITH (READCOMMITTEDLOCK)
            WHERE h.[InstanceId] = i.[InstanceId] AND h.[EventVersion] = i.[EventVersion] - 1))
        THROW 51304, 'Approval history versions are 1, 2, 3 with no gaps (DL-072).', 1;
END");
            migrationBuilder.Sql(@"CREATE TRIGGER [workflow].[TR_ApprovalInstance_Guarded] ON [workflow].[ApprovalInstance]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted)
        THROW 51305, 'workflow.ApprovalInstance rows cannot be deleted (DL-072).', 1;
    IF UPDATE([Id]) OR EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
            WHERE i.[RequestItemId] <> d.[RequestItemId] OR i.[Requester] <> d.[Requester] OR i.[Beneficiary] <> d.[Beneficiary])
        THROW 51306, 'An approval instance''s id, request item and people are immutable (DL-072).', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
            WHERE i.[EngineVersion] < d.[EngineVersion] OR i.[ActionCount] <= d.[ActionCount])
        THROW 51307, 'An approval instance only moves forward: every save adds actions (DL-072).', 1;
    IF EXISTS (SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id] WHERE d.[State] IN (N'Approved', N'Rejected', N'Cancelled', N'Withdrawn', N'Returned') AND i.[State] <> d.[State])
        THROW 51308, 'A closed approval instance never changes state (DL-072).', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [workflow].[TR_ApprovalInstance_Guarded];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [workflow].[TR_ApprovalHistory_AppendOnly];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [workflow].[TR_ApprovalAction_AppendOnly];");

            migrationBuilder.DropTable(
                name: "ApprovalAction",
                schema: "workflow");

            migrationBuilder.DropTable(
                name: "ApprovalHistory",
                schema: "workflow");

            migrationBuilder.DropTable(
                name: "ApprovalInstance",
                schema: "workflow");
        }
    }
}
