using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class ProvisioningScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-098, hand-written and reviewed (not generated). EntitlementOutOfScope is a block reason.
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] DROP CONSTRAINT [CK_ProvisioningItem_Shape];");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] ADD CONSTRAINT [CK_ProvisioningItem_Shape] CHECK (([State] = N'Blocked' AND [Problem] IS NOT NULL AND [Problem] IN (N'Integrity', N'NotAuthorized', N'ItemMissing', N'ItemMismatch', N'EntitlementChanged', N'AccountChanged', N'NoAccountName', N'NameInUse', N'ManualWork', N'AccountTaken', N'EntitlementOutOfScope') AND [ConnectorKey] IS NULL AND (([Problem] = N'Integrity' AND [IntegrityProblem] IS NOT NULL AND [ApprovalActions] IS NULL) OR ([Problem] <> N'Integrity' AND [IntegrityProblem] IS NULL AND [ApprovalActions] IS NOT NULL AND [ApprovalActions] >= 1))) OR ([State] IN (N'Planned', N'Running', N'Succeeded', N'Failed') AND [ApprovalActions] IS NOT NULL AND [ApprovalActions] >= 1 AND [IntegrityProblem] IS NULL AND [ConnectorKey] IS NOT NULL AND [Beneficiary] IS NOT NULL AND [ApplicationId] IS NOT NULL AND [EntitlementId] IS NOT NULL AND [EntitlementVersionId] IS NOT NULL AND (([State] = N'Failed' AND [Problem] IS NOT NULL) OR ([State] <> N'Failed' AND [Problem] IS NULL)) AND (([AccountId] IS NOT NULL AND [NewAccountType] IS NULL) OR ([AccountId] IS NULL AND [NewAccountType] IS NOT NULL AND [NewAccountType] IN (N'Primary', N'Secondary', N'Service', N'Shared') AND [AccountName] IS NOT NULL))));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] DROP CONSTRAINT [CK_ProvisioningItem_Shape];");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] ADD CONSTRAINT [CK_ProvisioningItem_Shape] CHECK (([State] = N'Blocked' AND [Problem] IS NOT NULL AND [Problem] IN (N'Integrity', N'NotAuthorized', N'ItemMissing', N'ItemMismatch', N'EntitlementChanged', N'AccountChanged', N'NoAccountName', N'NameInUse', N'ManualWork', N'AccountTaken') AND [ConnectorKey] IS NULL AND (([Problem] = N'Integrity' AND [IntegrityProblem] IS NOT NULL AND [ApprovalActions] IS NULL) OR ([Problem] <> N'Integrity' AND [IntegrityProblem] IS NULL AND [ApprovalActions] IS NOT NULL AND [ApprovalActions] >= 1))) OR ([State] IN (N'Planned', N'Running', N'Succeeded', N'Failed') AND [ApprovalActions] IS NOT NULL AND [ApprovalActions] >= 1 AND [IntegrityProblem] IS NULL AND [ConnectorKey] IS NOT NULL AND [Beneficiary] IS NOT NULL AND [ApplicationId] IS NOT NULL AND [EntitlementId] IS NOT NULL AND [EntitlementVersionId] IS NOT NULL AND (([State] = N'Failed' AND [Problem] IS NOT NULL) OR ([State] <> N'Failed' AND [Problem] IS NULL)) AND (([AccountId] IS NOT NULL AND [NewAccountType] IS NULL) OR ([AccountId] IS NULL AND [NewAccountType] IS NOT NULL AND [NewAccountType] IN (N'Primary', N'Secondary', N'Service', N'Shared') AND [AccountName] IS NOT NULL))));");
        }
    }
}
