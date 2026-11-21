using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class ProvisioningReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-096, hand-written and reviewed (not generated). The item shape is spelled out so no NULL makes it pass by being unknown
            // (a CHECK accepts UNKNOWN); AccountTaken is a block reason; a step above 10 only undoes a new account's access.
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] DROP CONSTRAINT [CK_ProvisioningItem_Shape];");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningOperation] DROP CONSTRAINT [CK_ProvisioningOperation_Shape];");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] ADD CONSTRAINT [CK_ProvisioningItem_Shape] CHECK (([State] = N'Blocked' AND [Problem] IS NOT NULL AND [Problem] IN (N'Integrity', N'NotAuthorized', N'ItemMissing', N'ItemMismatch', N'EntitlementChanged', N'AccountChanged', N'NoAccountName', N'NameInUse', N'ManualWork', N'AccountTaken') AND [ConnectorKey] IS NULL AND (([Problem] = N'Integrity' AND [IntegrityProblem] IS NOT NULL AND [ApprovalActions] IS NULL) OR ([Problem] <> N'Integrity' AND [IntegrityProblem] IS NULL AND [ApprovalActions] IS NOT NULL AND [ApprovalActions] >= 1))) OR ([State] IN (N'Planned', N'Running', N'Succeeded', N'Failed') AND [ApprovalActions] IS NOT NULL AND [ApprovalActions] >= 1 AND [IntegrityProblem] IS NULL AND [ConnectorKey] IS NOT NULL AND [Beneficiary] IS NOT NULL AND [ApplicationId] IS NOT NULL AND [EntitlementId] IS NOT NULL AND [EntitlementVersionId] IS NOT NULL AND (([State] = N'Failed' AND [Problem] IS NOT NULL) OR ([State] <> N'Failed' AND [Problem] IS NULL)) AND (([AccountId] IS NOT NULL AND [NewAccountType] IS NULL) OR ([AccountId] IS NULL AND [NewAccountType] IS NOT NULL AND [NewAccountType] IN (N'Primary', N'Secondary', N'Service', N'Shared') AND [AccountName] IS NOT NULL))));");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningOperation] ADD CONSTRAINT [CK_ProvisioningOperation_Shape] CHECK ((([Step] BETWEEN 1 AND 10 AND [Action] IN (N'account.create', N'account.entitlement.add', N'account.enable')) OR ([Step] = 11 AND [Action] = N'account.entitlement.remove') OR ([Step] = 12 AND [Action] = N'account.disable')) AND [State] IN (N'Planned', N'Running', N'Succeeded', N'Failed') AND [TargetType] = N'account' AND (([RelatedType] IS NULL AND [RelatedId] IS NULL) OR ([RelatedType] IN (N'container', N'entitlement') AND [RelatedId] IS NOT NULL AND LEN([RelatedId]) >= 1)) AND LEN([TargetId]) >= 1 AND [Attributes] LIKE N'{%}');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] DROP CONSTRAINT [CK_ProvisioningItem_Shape];");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningOperation] DROP CONSTRAINT [CK_ProvisioningOperation_Shape];");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningItem] ADD CONSTRAINT [CK_ProvisioningItem_Shape] CHECK (([State] = N'Blocked' AND [Problem] IS NOT NULL AND [Problem] IN (N'Integrity', N'NotAuthorized', N'ItemMissing', N'ItemMismatch', N'EntitlementChanged', N'AccountChanged', N'NoAccountName', N'NameInUse', N'ManualWork') AND [ConnectorKey] IS NULL AND (([Problem] = N'Integrity' AND [IntegrityProblem] IS NOT NULL AND [ApprovalActions] IS NULL) OR ([Problem] <> N'Integrity' AND [IntegrityProblem] IS NULL AND [ApprovalActions] IS NOT NULL AND [ApprovalActions] >= 1))) OR ([State] IN (N'Planned', N'Running', N'Succeeded', N'Failed') AND [ApprovalActions] IS NOT NULL AND [ApprovalActions] >= 1 AND [IntegrityProblem] IS NULL AND [ConnectorKey] IS NOT NULL AND [Beneficiary] IS NOT NULL AND [ApplicationId] IS NOT NULL AND [EntitlementId] IS NOT NULL AND [EntitlementVersionId] IS NOT NULL AND (([State] = N'Failed' AND [Problem] IS NOT NULL) OR ([State] <> N'Failed' AND [Problem] IS NULL)) AND (([AccountId] IS NOT NULL AND [NewAccountType] IS NULL) OR ([AccountId] IS NULL AND [NewAccountType] IS NOT NULL AND [NewAccountType] IN (N'Primary', N'Secondary', N'Service', N'Shared') AND [AccountName] IS NOT NULL))));");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[ProvisioningOperation] ADD CONSTRAINT [CK_ProvisioningOperation_Shape] CHECK (([Step] BETWEEN 1 AND 10 AND [Action] IN (N'account.create', N'account.entitlement.add', N'account.enable')) AND [State] IN (N'Planned', N'Running', N'Succeeded', N'Failed') AND [TargetType] = N'account' AND (([RelatedType] IS NULL AND [RelatedId] IS NULL) OR ([RelatedType] IN (N'container', N'entitlement') AND [RelatedId] IS NOT NULL AND LEN([RelatedId]) >= 1)) AND LEN([TargetId]) >= 1 AND [Attributes] LIKE N'{%}');");
        }
    }
}
