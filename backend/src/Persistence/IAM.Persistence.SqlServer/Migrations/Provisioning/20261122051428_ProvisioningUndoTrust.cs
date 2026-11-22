using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class ProvisioningUndoTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Postponements",
                schema: "provisioning",
                table: "ProvisioningItem",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UndoAttempts",
                schema: "provisioning",
                table: "ProvisioningItem",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "UndoReason",
                schema: "provisioning",
                table: "ProvisioningItem",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateTable(
                name: "GrantLock",
                schema: "provisioning",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntitlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HeldBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrantLock", x => new { x.AccountId, x.EntitlementId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningItem_AccountId_EntitlementId",
                schema: "provisioning",
                table: "ProvisioningItem",
                columns: new[] { "AccountId", "EntitlementId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProvisioningItem_UndoReason_Code",
                schema: "provisioning",
                table: "ProvisioningItem",
                sql: "DATALENGTH([UndoReason]) > 0 AND DATALENGTH([UndoReason]) = DATALENGTH(LTRIM(RTRIM([UndoReason])))");
            // DL-100, hand-written and reviewed (not generated): undo passes, postponements and the undo reason stay in range; a
            // grant lock names an account, an entitlement and its holder.
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] ADD CONSTRAINT [CK_ProvisioningItem_Undo] CHECK ([UndoAttempts] BETWEEN 0 AND 1000 AND [Postponements] BETWEEN 0 AND 1000 AND ([UndoReason] IS NULL OR [UndoAttempts] >= 1));");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[GrantLock] ADD CONSTRAINT [CK_GrantLock_Ids] CHECK ([AccountId] <> '00000000-0000-0000-0000-000000000000' AND [EntitlementId] <> '00000000-0000-0000-0000-000000000000' AND [HeldBy] <> '00000000-0000-0000-0000-000000000000');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020): Down is for an empty development database; with data, recover by restore and a compensating
            // forward migration.
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] DROP CONSTRAINT [CK_ProvisioningItem_Undo];");
            migrationBuilder.DropTable(
                name: "GrantLock",
                schema: "provisioning");

            migrationBuilder.DropIndex(
                name: "IX_ProvisioningItem_AccountId_EntitlementId",
                schema: "provisioning",
                table: "ProvisioningItem");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProvisioningItem_UndoReason_Code",
                schema: "provisioning",
                table: "ProvisioningItem");

            migrationBuilder.DropColumn(
                name: "Postponements",
                schema: "provisioning",
                table: "ProvisioningItem");

            migrationBuilder.DropColumn(
                name: "UndoAttempts",
                schema: "provisioning",
                table: "ProvisioningItem");

            migrationBuilder.DropColumn(
                name: "UndoReason",
                schema: "provisioning",
                table: "ProvisioningItem");
        }
    }
}
