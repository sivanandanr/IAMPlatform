using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class ProvisioningUndo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-099, hand-written and reviewed (not generated). NameNotApproved is a block reason.
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item DROP CONSTRAINT ck_provisioning_item_shape;");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item ADD CONSTRAINT ck_provisioning_item_shape CHECK ((state = 'Blocked' AND problem IS NOT NULL AND problem IN ('Integrity', 'NotAuthorized', 'ItemMissing', 'ItemMismatch', 'EntitlementChanged', 'AccountChanged', 'NoAccountName', 'NameInUse', 'ManualWork', 'AccountTaken', 'EntitlementOutOfScope', 'NameNotApproved') AND connector_key IS NULL AND ((problem = 'Integrity' AND integrity_problem IS NOT NULL AND approval_actions IS NULL) OR (problem <> 'Integrity' AND integrity_problem IS NULL AND approval_actions IS NOT NULL AND approval_actions >= 1))) OR (state IN ('Planned', 'Running', 'Succeeded', 'Failed') AND approval_actions IS NOT NULL AND approval_actions >= 1 AND integrity_problem IS NULL AND connector_key IS NOT NULL AND beneficiary IS NOT NULL AND application_id IS NOT NULL AND entitlement_id IS NOT NULL AND entitlement_version_id IS NOT NULL AND ((state = 'Failed' AND problem IS NOT NULL) OR (state <> 'Failed' AND problem IS NULL)) AND ((account_id IS NOT NULL AND new_account_type IS NULL) OR (account_id IS NULL AND new_account_type IS NOT NULL AND new_account_type IN ('Primary', 'Secondary', 'Service', 'Shared') AND account_name IS NOT NULL))));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020, DL-099): Down restores the earlier rules for an empty development database only; with data, recover by
            // restore and a compensating forward migration.
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item DROP CONSTRAINT ck_provisioning_item_shape;");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item ADD CONSTRAINT ck_provisioning_item_shape CHECK ((state = 'Blocked' AND problem IS NOT NULL AND problem IN ('Integrity', 'NotAuthorized', 'ItemMissing', 'ItemMismatch', 'EntitlementChanged', 'AccountChanged', 'NoAccountName', 'NameInUse', 'ManualWork', 'AccountTaken', 'EntitlementOutOfScope') AND connector_key IS NULL AND ((problem = 'Integrity' AND integrity_problem IS NOT NULL AND approval_actions IS NULL) OR (problem <> 'Integrity' AND integrity_problem IS NULL AND approval_actions IS NOT NULL AND approval_actions >= 1))) OR (state IN ('Planned', 'Running', 'Succeeded', 'Failed') AND approval_actions IS NOT NULL AND approval_actions >= 1 AND integrity_problem IS NULL AND connector_key IS NOT NULL AND beneficiary IS NOT NULL AND application_id IS NOT NULL AND entitlement_id IS NOT NULL AND entitlement_version_id IS NOT NULL AND ((state = 'Failed' AND problem IS NOT NULL) OR (state <> 'Failed' AND problem IS NULL)) AND ((account_id IS NOT NULL AND new_account_type IS NULL) OR (account_id IS NULL AND new_account_type IS NOT NULL AND new_account_type IN ('Primary', 'Secondary', 'Service', 'Shared') AND account_name IS NOT NULL))));");
        }
    }
}
