using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class ProvisioningRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAtUtc",
                schema: "provisioning",
                table: "ProvisioningOperation",
                type: "datetimeoffset(6)",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultCode",
                schema: "provisioning",
                table: "ProvisioningOperation",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                schema: "provisioning",
                table: "ProvisioningItem",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                schema: "provisioning",
                table: "ProvisioningItem",
                type: "nchar(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinishedAtUtc",
                schema: "provisioning",
                table: "ProvisioningItem",
                type: "datetimeoffset(6)",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseUntilUtc",
                schema: "provisioning",
                table: "ProvisioningItem",
                type: "datetimeoffset(6)",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAtUtc",
                schema: "provisioning",
                table: "ProvisioningItem",
                type: "datetimeoffset(6)",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecordedAccountId",
                schema: "provisioning",
                table: "ProvisioningItem",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProvisioningOperation_ResultCode_Code",
                schema: "provisioning",
                table: "ProvisioningOperation",
                sql: "DATALENGTH([ResultCode]) > 0 AND DATALENGTH([ResultCode]) = DATALENGTH(LTRIM(RTRIM([ResultCode])))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProvisioningItem_CorrelationId_Code",
                schema: "provisioning",
                table: "ProvisioningItem",
                sql: "DATALENGTH([CorrelationId]) > 0 AND DATALENGTH([CorrelationId]) = DATALENGTH(LTRIM(RTRIM([CorrelationId])))");
            // DL-094, hand-written and reviewed (not generated); the DL-093 guards stay as they are. The correlation id is set once
            // and never changes; attempts never go down; the recorded account, a step's result and the finishing times are set once;
            // a succeeded item has finished, and a new account's has its recorded account. SQL Server compares null-safely.
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] ADD CONSTRAINT [CK_ProvisioningItem_Run] CHECK ([Attempts] BETWEEN 0 AND 1000 AND ([CorrelationId] IS NULL OR (LEN([CorrelationId]) = 32 AND [CorrelationId] NOT LIKE '%[^0-9a-f]%')) AND ([State] <> N'Succeeded' OR ([FinishedAtUtc] IS NOT NULL AND ([NewAccountType] IS NULL OR [RecordedAccountId] IS NOT NULL))) AND ([RecordedAccountId] IS NULL OR ([State] = N'Succeeded' AND [NewAccountType] IS NOT NULL)) AND ([LeaseUntilUtc] IS NULL OR [State] = N'Running'));");
            migrationBuilder.Sql(@"CREATE TRIGGER [provisioning].[TR_ProvisioningItem_Run] ON [provisioning].[ProvisioningItem]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
            WHERE NOT (EXISTS (SELECT i.[CorrelationId] INTERSECT SELECT d.[CorrelationId])
                AND i.[Attempts] >= d.[Attempts]
                AND (d.[RecordedAccountId] IS NULL OR EXISTS (SELECT i.[RecordedAccountId] INTERSECT SELECT d.[RecordedAccountId]))
                AND (d.[FinishedAtUtc] IS NULL OR EXISTS (SELECT i.[FinishedAtUtc] INTERSECT SELECT d.[FinishedAtUtc]))))
        THROW 51509, 'A provisioning item''s correlation id never changes, its attempts never go down, and what it finished with is kept (DL-094).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [provisioning].[TR_ProvisioningOperation_Run] ON [provisioning].[ProvisioningOperation]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
            WHERE NOT ((d.[ResultCode] IS NULL OR EXISTS (SELECT i.[ResultCode] INTERSECT SELECT d.[ResultCode])) AND (d.[CompletedAtUtc] IS NULL OR EXISTS (SELECT i.[CompletedAtUtc] INTERSECT SELECT d.[CompletedAtUtc]))))
        THROW 51510, 'A step''s result is kept once it finished (DL-094).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [provisioning].[TR_ProvisioningOperation_Run];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [provisioning].[TR_ProvisioningItem_Run];");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] DROP CONSTRAINT [CK_ProvisioningItem_Run];");
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProvisioningOperation_ResultCode_Code",
                schema: "provisioning",
                table: "ProvisioningOperation");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProvisioningItem_CorrelationId_Code",
                schema: "provisioning",
                table: "ProvisioningItem");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                schema: "provisioning",
                table: "ProvisioningOperation");

            migrationBuilder.DropColumn(
                name: "ResultCode",
                schema: "provisioning",
                table: "ProvisioningOperation");

            migrationBuilder.DropColumn(
                name: "Attempts",
                schema: "provisioning",
                table: "ProvisioningItem");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                schema: "provisioning",
                table: "ProvisioningItem");

            migrationBuilder.DropColumn(
                name: "FinishedAtUtc",
                schema: "provisioning",
                table: "ProvisioningItem");

            migrationBuilder.DropColumn(
                name: "LeaseUntilUtc",
                schema: "provisioning",
                table: "ProvisioningItem");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtc",
                schema: "provisioning",
                table: "ProvisioningItem");

            migrationBuilder.DropColumn(
                name: "RecordedAccountId",
                schema: "provisioning",
                table: "ProvisioningItem");
        }
    }
}
