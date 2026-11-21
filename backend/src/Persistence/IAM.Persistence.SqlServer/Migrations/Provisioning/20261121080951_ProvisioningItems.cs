using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class ProvisioningItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "provisioning");

            migrationBuilder.CreateTable(
                name: "InboxMessage",
                schema: "provisioning",
                columns: table => new
                {
                    ConsumerName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessage", x => new { x.ConsumerName, x.MessageId });
                    table.CheckConstraint("CK_InboxMessage_ConsumerName_Code", "DATALENGTH([ConsumerName]) > 0 AND DATALENGTH([ConsumerName]) = DATALENGTH(LTRIM(RTRIM([ConsumerName])))");
                    table.CheckConstraint("CK_InboxMessage_IdempotencyKey_Code", "DATALENGTH([IdempotencyKey]) > 0 AND DATALENGTH([IdempotencyKey]) = DATALENGTH(LTRIM(RTRIM([IdempotencyKey])))");
                    table.CheckConstraint("CK_InboxMessage_Source_Code", "DATALENGTH([Source]) > 0 AND DATALENGTH([Source]) = DATALENGTH(LTRIM(RTRIM([Source])))");
                    table.CheckConstraint("CK_InboxMessage_Type_Code", "DATALENGTH([Type]) > 0 AND DATALENGTH([Type]) = DATALENGTH(LTRIM(RTRIM([Type])))");
                });

            migrationBuilder.CreateTable(
                name: "OperationLedger",
                schema: "provisioning",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fingerprint = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationLedger", x => x.OperationId);
                    table.CheckConstraint("CK_OperationLedger_Code_Code", "DATALENGTH([Code]) > 0 AND DATALENGTH([Code]) = DATALENGTH(LTRIM(RTRIM([Code])))");
                    table.CheckConstraint("CK_OperationLedger_Fingerprint_Code", "DATALENGTH([Fingerprint]) > 0 AND DATALENGTH([Fingerprint]) = DATALENGTH(LTRIM(RTRIM([Fingerprint])))");
                    table.CheckConstraint("CK_OperationLedger_Status_Code", "DATALENGTH([Status]) > 0 AND DATALENGTH([Status]) = DATALENGTH(LTRIM(RTRIM([Status])))");
                });

            migrationBuilder.CreateTable(
                name: "ProvisioningItem",
                schema: "provisioning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovalActions = table.Column<int>(type: "int", nullable: true),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Problem = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    IntegrityProblem = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Beneficiary = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EntitlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EntitlementVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NewAccountType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true, collation: "Latin1_General_100_BIN2"),
                    AccountName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ConnectorKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningItem", x => x.Id);
                    table.CheckConstraint("CK_ProvisioningItem_AccountName_Code", "DATALENGTH([AccountName]) > 0 AND DATALENGTH([AccountName]) = DATALENGTH(LTRIM(RTRIM([AccountName])))");
                    table.CheckConstraint("CK_ProvisioningItem_Beneficiary_Code", "DATALENGTH([Beneficiary]) > 0 AND DATALENGTH([Beneficiary]) = DATALENGTH(LTRIM(RTRIM([Beneficiary])))");
                    table.CheckConstraint("CK_ProvisioningItem_ConnectorKey_Code", "DATALENGTH([ConnectorKey]) > 0 AND DATALENGTH([ConnectorKey]) = DATALENGTH(LTRIM(RTRIM([ConnectorKey])))");
                    table.CheckConstraint("CK_ProvisioningItem_IntegrityProblem_Code", "DATALENGTH([IntegrityProblem]) > 0 AND DATALENGTH([IntegrityProblem]) = DATALENGTH(LTRIM(RTRIM([IntegrityProblem])))");
                    table.CheckConstraint("CK_ProvisioningItem_NewAccountType_Code", "DATALENGTH([NewAccountType]) > 0 AND DATALENGTH([NewAccountType]) = DATALENGTH(LTRIM(RTRIM([NewAccountType])))");
                    table.CheckConstraint("CK_ProvisioningItem_Problem_Code", "DATALENGTH([Problem]) > 0 AND DATALENGTH([Problem]) = DATALENGTH(LTRIM(RTRIM([Problem])))");
                    table.CheckConstraint("CK_ProvisioningItem_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                });

            migrationBuilder.CreateTable(
                name: "ProvisioningOperation",
                schema: "provisioning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Step = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TargetType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TargetId = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    RelatedType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    RelatedId = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Attributes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningOperation", x => x.Id);
                    table.CheckConstraint("CK_ProvisioningOperation_Action_Code", "DATALENGTH([Action]) > 0 AND DATALENGTH([Action]) = DATALENGTH(LTRIM(RTRIM([Action])))");
                    table.CheckConstraint("CK_ProvisioningOperation_RelatedType_Code", "DATALENGTH([RelatedType]) > 0 AND DATALENGTH([RelatedType]) = DATALENGTH(LTRIM(RTRIM([RelatedType])))");
                    table.CheckConstraint("CK_ProvisioningOperation_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.CheckConstraint("CK_ProvisioningOperation_TargetType_Code", "DATALENGTH([TargetType]) > 0 AND DATALENGTH([TargetType]) = DATALENGTH(LTRIM(RTRIM([TargetType])))");
                    table.ForeignKey(
                        name: "FK_ProvisioningOperation_ProvisioningItem_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "provisioning",
                        principalTable: "ProvisioningItem",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessage_ConsumerName_Source_IdempotencyKey",
                schema: "provisioning",
                table: "InboxMessage",
                columns: new[] { "ConsumerName", "Source", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningItem_State_ReceivedAtUtc",
                schema: "provisioning",
                table: "ProvisioningItem",
                columns: new[] { "State", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningOperation_ItemId_Step",
                schema: "provisioning",
                table: "ProvisioningOperation",
                columns: new[] { "ItemId", "Step" },
                unique: true);
            // DL-093, hand-written and reviewed (not generated). What an authorised item was planned from, and each planned
            // operation, never change; states only move forward (Planned to Running, Running back to Planned or on to
            // Succeeded or Failed; Blocked never changes); nothing is deleted. The ledger and the inbox are append-only.
            // Account names are checked as accounts' are (hyphen first in the SQL Server pattern, DL-091).
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] ADD CONSTRAINT [CK_ProvisioningItem_Shape] CHECK (([State] = N'Blocked' AND ([ApprovalActions] IS NULL OR [ApprovalActions] >= 1) AND [Problem] IN (N'Integrity', N'NotAuthorized', N'ItemMissing', N'ItemMismatch', N'EntitlementChanged', N'AccountChanged', N'NoAccountName', N'NameInUse', N'ManualWork') AND [ConnectorKey] IS NULL AND (([Problem] = N'Integrity' AND [IntegrityProblem] IS NOT NULL AND [ApprovalActions] IS NULL) OR ([Problem] <> N'Integrity' AND [IntegrityProblem] IS NULL AND [ApprovalActions] IS NOT NULL))) OR ([State] IN (N'Planned', N'Running', N'Succeeded', N'Failed') AND [ApprovalActions] >= 1 AND [IntegrityProblem] IS NULL AND [ConnectorKey] IS NOT NULL AND [Beneficiary] IS NOT NULL AND [ApplicationId] IS NOT NULL AND [EntitlementId] IS NOT NULL AND [EntitlementVersionId] IS NOT NULL AND (([State] = N'Failed' AND [Problem] IS NOT NULL) OR ([State] <> N'Failed' AND [Problem] IS NULL)) AND (([AccountId] IS NOT NULL AND [NewAccountType] IS NULL) OR ([AccountId] IS NULL AND [NewAccountType] IN (N'Primary', N'Secondary', N'Service', N'Shared') AND [AccountName] IS NOT NULL))));");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] ADD CONSTRAINT [CK_ProvisioningItem_Name] CHECK ([AccountName] IS NULL OR (LEN([AccountName]) BETWEEN 1 AND 256 AND DATALENGTH([AccountName]) = 2 * LEN([AccountName]) AND [AccountName] NOT LIKE '[-._]%' AND [AccountName] NOT LIKE '%[^-A-Za-z0-9._]%'));");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningOperation] ADD CONSTRAINT [CK_ProvisioningOperation_Shape] CHECK ([Step] BETWEEN 1 AND 10 AND [State] IN (N'Planned', N'Running', N'Succeeded', N'Failed') AND [Action] IN (N'account.create', N'account.entitlement.add', N'account.enable') AND [TargetType] = N'account' AND ([RelatedType] IS NULL OR [RelatedType] IN (N'container', N'entitlement')) AND (([RelatedType] IS NULL AND [RelatedId] IS NULL) OR ([RelatedType] IS NOT NULL AND LEN([RelatedId]) >= 1)) AND LEN([TargetId]) >= 1 AND [Attributes] LIKE N'{%}');");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[OperationLedger] ADD CONSTRAINT [CK_OperationLedger_Values] CHECK (LEN([Fingerprint]) = 64 AND [Fingerprint] NOT LIKE '%[^0-9a-f]%' AND [Status] IN (N'Applied', N'AlreadyInState', N'Rejected', N'Failed', N'Unsupported'));");
            migrationBuilder.Sql(@"CREATE TRIGGER [provisioning].[TR_ProvisioningItem_Guarded] ON [provisioning].[ProvisioningItem]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
        THROW 51501, 'provisioning.ProvisioningItem rows cannot be deleted (DL-093).', 1;
    IF UPDATE([Id]) OR EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
            WHERE NOT (EXISTS (SELECT i.[ApprovalActions] INTERSECT SELECT d.[ApprovalActions])
                AND EXISTS (SELECT i.[IntegrityProblem] INTERSECT SELECT d.[IntegrityProblem])
                AND EXISTS (SELECT i.[Beneficiary] INTERSECT SELECT d.[Beneficiary])
                AND EXISTS (SELECT i.[ApplicationId] INTERSECT SELECT d.[ApplicationId])
                AND EXISTS (SELECT i.[EntitlementId] INTERSECT SELECT d.[EntitlementId])
                AND EXISTS (SELECT i.[EntitlementVersionId] INTERSECT SELECT d.[EntitlementVersionId])
                AND EXISTS (SELECT i.[AccountId] INTERSECT SELECT d.[AccountId])
                AND EXISTS (SELECT i.[NewAccountType] INTERSECT SELECT d.[NewAccountType])
                AND EXISTS (SELECT i.[AccountName] INTERSECT SELECT d.[AccountName])
                AND EXISTS (SELECT i.[ConnectorKey] INTERSECT SELECT d.[ConnectorKey])
                AND EXISTS (SELECT i.[ReceivedAtUtc] INTERSECT SELECT d.[ReceivedAtUtc])
                AND EXISTS (SELECT i.[CreatedAtUtc] INTERSECT SELECT d.[CreatedAtUtc])
                AND EXISTS (SELECT CAST(i.[CreatedBy] AS varbinary(max)) INTERSECT SELECT CAST(d.[CreatedBy] AS varbinary(max)))))
        THROW 51502, 'What a provisioning item was planned from never changes (DL-093).', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
            WHERE NOT ((d.[State] = i.[State] AND EXISTS (SELECT i.[Problem] INTERSECT SELECT d.[Problem]))
                OR (d.[State] = N'Planned' AND i.[State] = N'Running' AND i.[Problem] IS NULL)
                OR (d.[State] = N'Running' AND i.[State] IN (N'Planned', N'Succeeded') AND i.[Problem] IS NULL)
                OR (d.[State] = N'Running' AND i.[State] = N'Failed')))
        THROW 51503, 'A provisioning item''s state only moves forward (DL-093).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [provisioning].[TR_ProvisioningOperation_Guarded] ON [provisioning].[ProvisioningOperation]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
        THROW 51504, 'provisioning.ProvisioningOperation rows cannot be deleted (DL-093).', 1;
    IF UPDATE([Id]) OR EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
            WHERE NOT (EXISTS (SELECT i.[ItemId] INTERSECT SELECT d.[ItemId])
                AND EXISTS (SELECT i.[Step] INTERSECT SELECT d.[Step])
                AND EXISTS (SELECT i.[Action] INTERSECT SELECT d.[Action])
                AND EXISTS (SELECT i.[TargetType] INTERSECT SELECT d.[TargetType])
                AND EXISTS (SELECT CAST(i.[TargetId] AS varbinary(max)) INTERSECT SELECT CAST(d.[TargetId] AS varbinary(max)))
                AND EXISTS (SELECT i.[RelatedType] INTERSECT SELECT d.[RelatedType])
                AND EXISTS (SELECT CAST(i.[RelatedId] AS varbinary(max)) INTERSECT SELECT CAST(d.[RelatedId] AS varbinary(max)))
                AND EXISTS (SELECT CAST(i.[Attributes] AS varbinary(max)) INTERSECT SELECT CAST(d.[Attributes] AS varbinary(max)))
                AND EXISTS (SELECT i.[CreatedAtUtc] INTERSECT SELECT d.[CreatedAtUtc])
                AND EXISTS (SELECT CAST(i.[CreatedBy] AS varbinary(max)) INTERSECT SELECT CAST(d.[CreatedBy] AS varbinary(max)))))
        THROW 51505, 'A planned operation never changes (DL-093).', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
            WHERE NOT ((d.[State] = i.[State])
                OR (d.[State] = N'Planned' AND i.[State] = N'Running')
                OR (d.[State] = N'Running' AND i.[State] IN (N'Planned', N'Succeeded', N'Failed'))))
        THROW 51506, 'An operation''s state only moves forward (DL-093).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [provisioning].[TR_OperationLedger_AppendOnly] ON [provisioning].[OperationLedger]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51507, 'provisioning.OperationLedger rows are append-only (DL-093).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [provisioning].[TR_InboxMessage_AppendOnly] ON [provisioning].[InboxMessage]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51508, 'provisioning.InboxMessage rows are append-only (DL-078, DL-093).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the provisioning schema's guards; only valid on a database that holds no provisioning worth keeping (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [provisioning].[TR_InboxMessage_AppendOnly];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [provisioning].[TR_OperationLedger_AppendOnly];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [provisioning].[TR_ProvisioningOperation_Guarded];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [provisioning].[TR_ProvisioningItem_Guarded];");
            migrationBuilder.DropTable(
                name: "InboxMessage",
                schema: "provisioning");

            migrationBuilder.DropTable(
                name: "OperationLedger",
                schema: "provisioning");

            migrationBuilder.DropTable(
                name: "ProvisioningOperation",
                schema: "provisioning");

            migrationBuilder.DropTable(
                name: "ProvisioningItem",
                schema: "provisioning");
        }
    }
}
