using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class ProvisioningReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-096, hand-written and reviewed (not generated). The item shape is spelled out so no NULL makes it pass by being unknown
            // (a CHECK accepts UNKNOWN); AccountTaken is a block reason; a step above 10 only undoes a new account's access.
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item DROP CONSTRAINT ck_provisioning_item_shape;");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_operation DROP CONSTRAINT ck_provisioning_operation_shape;");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item ADD CONSTRAINT ck_provisioning_item_shape CHECK ((state = 'Blocked' AND problem IS NOT NULL AND problem IN ('Integrity', 'NotAuthorized', 'ItemMissing', 'ItemMismatch', 'EntitlementChanged', 'AccountChanged', 'NoAccountName', 'NameInUse', 'ManualWork', 'AccountTaken') AND connector_key IS NULL AND ((problem = 'Integrity' AND integrity_problem IS NOT NULL AND approval_actions IS NULL) OR (problem <> 'Integrity' AND integrity_problem IS NULL AND approval_actions IS NOT NULL AND approval_actions >= 1))) OR (state IN ('Planned', 'Running', 'Succeeded', 'Failed') AND approval_actions IS NOT NULL AND approval_actions >= 1 AND integrity_problem IS NULL AND connector_key IS NOT NULL AND beneficiary IS NOT NULL AND application_id IS NOT NULL AND entitlement_id IS NOT NULL AND entitlement_version_id IS NOT NULL AND ((state = 'Failed' AND problem IS NOT NULL) OR (state <> 'Failed' AND problem IS NULL)) AND ((account_id IS NOT NULL AND new_account_type IS NULL) OR (account_id IS NULL AND new_account_type IS NOT NULL AND new_account_type IN ('Primary', 'Secondary', 'Service', 'Shared') AND account_name IS NOT NULL))));");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_operation ADD CONSTRAINT ck_provisioning_operation_shape CHECK (((step BETWEEN 1 AND 10 AND action IN ('account.create', 'account.entitlement.add', 'account.enable')) OR (step = 11 AND action = 'account.entitlement.remove') OR (step = 12 AND action = 'account.disable')) AND state IN ('Planned', 'Running', 'Succeeded', 'Failed') AND target_type = 'account' AND ((related_type IS NULL AND related_id IS NULL) OR (related_type IN ('container', 'entitlement') AND related_id IS NOT NULL AND length(related_id) >= 1)) AND length(target_id) >= 1 AND attributes LIKE '{%}');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item DROP CONSTRAINT ck_provisioning_item_shape;");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_operation DROP CONSTRAINT ck_provisioning_operation_shape;");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item ADD CONSTRAINT ck_provisioning_item_shape CHECK ((state = 'Blocked' AND problem IS NOT NULL AND problem IN ('Integrity', 'NotAuthorized', 'ItemMissing', 'ItemMismatch', 'EntitlementChanged', 'AccountChanged', 'NoAccountName', 'NameInUse', 'ManualWork') AND connector_key IS NULL AND ((problem = 'Integrity' AND integrity_problem IS NOT NULL AND approval_actions IS NULL) OR (problem <> 'Integrity' AND integrity_problem IS NULL AND approval_actions IS NOT NULL AND approval_actions >= 1))) OR (state IN ('Planned', 'Running', 'Succeeded', 'Failed') AND approval_actions IS NOT NULL AND approval_actions >= 1 AND integrity_problem IS NULL AND connector_key IS NOT NULL AND beneficiary IS NOT NULL AND application_id IS NOT NULL AND entitlement_id IS NOT NULL AND entitlement_version_id IS NOT NULL AND ((state = 'Failed' AND problem IS NOT NULL) OR (state <> 'Failed' AND problem IS NULL)) AND ((account_id IS NOT NULL AND new_account_type IS NULL) OR (account_id IS NULL AND new_account_type IS NOT NULL AND new_account_type IN ('Primary', 'Secondary', 'Service', 'Shared') AND account_name IS NOT NULL))));");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_operation ADD CONSTRAINT ck_provisioning_operation_shape CHECK ((step BETWEEN 1 AND 10 AND action IN ('account.create', 'account.entitlement.add', 'account.enable')) AND state IN ('Planned', 'Running', 'Succeeded', 'Failed') AND target_type = 'account' AND ((related_type IS NULL AND related_id IS NULL) OR (related_type IN ('container', 'entitlement') AND related_id IS NOT NULL AND length(related_id) >= 1)) AND length(target_id) >= 1 AND attributes LIKE '{%}');");
        }
    }
}
