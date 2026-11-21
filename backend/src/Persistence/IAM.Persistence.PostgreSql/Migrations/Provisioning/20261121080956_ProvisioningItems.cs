using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Provisioning
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
                name: "inbox_message",
                schema: "provisioning",
                columns: table => new
                {
                    consumer_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, collation: "C"),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_message", x => new { x.consumer_name, x.message_id });
                    table.CheckConstraint("ck_inbox_message_consumer_name_code", "\"consumer_name\" <> '' AND \"consumer_name\" = btrim(\"consumer_name\")");
                    table.CheckConstraint("ck_inbox_message_idempotency_key_code", "\"idempotency_key\" <> '' AND \"idempotency_key\" = btrim(\"idempotency_key\")");
                    table.CheckConstraint("ck_inbox_message_source_code", "\"source\" <> '' AND \"source\" = btrim(\"source\")");
                    table.CheckConstraint("ck_inbox_message_type_code", "\"type\" <> '' AND \"type\" = btrim(\"type\")");
                });

            migrationBuilder.CreateTable(
                name: "operation_ledger",
                schema: "provisioning",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operation_ledger", x => x.operation_id);
                    table.CheckConstraint("ck_operation_ledger_code_code", "\"code\" <> '' AND \"code\" = btrim(\"code\")");
                    table.CheckConstraint("ck_operation_ledger_fingerprint_code", "\"fingerprint\" <> '' AND \"fingerprint\" = btrim(\"fingerprint\")");
                    table.CheckConstraint("ck_operation_ledger_status_code", "\"status\" <> '' AND \"status\" = btrim(\"status\")");
                });

            migrationBuilder.CreateTable(
                name: "provisioning_item",
                schema: "provisioning",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_actions = table.Column<int>(type: "integer", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    problem = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    integrity_problem = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    beneficiary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entitlement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entitlement_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_account_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    account_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true, collation: "C"),
                    connector_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provisioning_item", x => x.id);
                    table.CheckConstraint("ck_provisioning_item_account_name_code", "\"account_name\" <> '' AND \"account_name\" = btrim(\"account_name\")");
                    table.CheckConstraint("ck_provisioning_item_beneficiary_code", "\"beneficiary\" <> '' AND \"beneficiary\" = btrim(\"beneficiary\")");
                    table.CheckConstraint("ck_provisioning_item_connector_key_code", "\"connector_key\" <> '' AND \"connector_key\" = btrim(\"connector_key\")");
                    table.CheckConstraint("ck_provisioning_item_integrity_problem_code", "\"integrity_problem\" <> '' AND \"integrity_problem\" = btrim(\"integrity_problem\")");
                    table.CheckConstraint("ck_provisioning_item_new_account_type_code", "\"new_account_type\" <> '' AND \"new_account_type\" = btrim(\"new_account_type\")");
                    table.CheckConstraint("ck_provisioning_item_problem_code", "\"problem\" <> '' AND \"problem\" = btrim(\"problem\")");
                    table.CheckConstraint("ck_provisioning_item_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                });

            migrationBuilder.CreateTable(
                name: "provisioning_operation",
                schema: "provisioning",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    target_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    target_id = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    related_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    related_id = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    attributes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provisioning_operation", x => x.id);
                    table.CheckConstraint("ck_provisioning_operation_action_code", "\"action\" <> '' AND \"action\" = btrim(\"action\")");
                    table.CheckConstraint("ck_provisioning_operation_related_type_code", "\"related_type\" <> '' AND \"related_type\" = btrim(\"related_type\")");
                    table.CheckConstraint("ck_provisioning_operation_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.CheckConstraint("ck_provisioning_operation_target_type_code", "\"target_type\" <> '' AND \"target_type\" = btrim(\"target_type\")");
                    table.ForeignKey(
                        name: "fk_provisioning_operation_provisioning_item_item_id",
                        column: x => x.item_id,
                        principalSchema: "provisioning",
                        principalTable: "provisioning_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_message_consumer_name_source_idempotency_key",
                schema: "provisioning",
                table: "inbox_message",
                columns: new[] { "consumer_name", "source", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_item_state_received_at_utc",
                schema: "provisioning",
                table: "provisioning_item",
                columns: new[] { "state", "received_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_operation_item_id_step",
                schema: "provisioning",
                table: "provisioning_operation",
                columns: new[] { "item_id", "step" },
                unique: true);
            // DL-093, hand-written and reviewed (not generated). What an authorised item was planned from, and each planned
            // operation, never change; states only move forward (Planned to Running, Running back to Planned or on to
            // Succeeded or Failed; Blocked never changes); nothing is deleted. The ledger and the inbox are append-only.
            // Account names are checked as accounts' are (hyphen first in the SQL Server pattern, DL-091).
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item ADD CONSTRAINT ck_provisioning_item_shape CHECK ((state = 'Blocked' AND (approval_actions IS NULL OR approval_actions >= 1) AND problem IN ('Integrity', 'NotAuthorized', 'ItemMissing', 'ItemMismatch', 'EntitlementChanged', 'AccountChanged', 'NoAccountName', 'NameInUse', 'ManualWork') AND connector_key IS NULL AND (problem = 'Integrity') = (integrity_problem IS NOT NULL) AND (problem = 'Integrity') = (approval_actions IS NULL)) OR (state IN ('Planned', 'Running', 'Succeeded', 'Failed') AND approval_actions >= 1 AND integrity_problem IS NULL AND connector_key IS NOT NULL AND beneficiary IS NOT NULL AND application_id IS NOT NULL AND entitlement_id IS NOT NULL AND entitlement_version_id IS NOT NULL AND (state = 'Failed') = (problem IS NOT NULL) AND ((account_id IS NOT NULL AND new_account_type IS NULL) OR (account_id IS NULL AND new_account_type IN ('Primary', 'Secondary', 'Service', 'Shared') AND account_name IS NOT NULL))));");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item ADD CONSTRAINT ck_provisioning_item_name CHECK (account_name IS NULL OR account_name ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,255}$');");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_operation ADD CONSTRAINT ck_provisioning_operation_shape CHECK (step BETWEEN 1 AND 10 AND state IN ('Planned', 'Running', 'Succeeded', 'Failed') AND action IN ('account.create', 'account.entitlement.add', 'account.enable') AND target_type = 'account' AND (related_type IS NULL OR related_type IN ('container', 'entitlement')) AND ((related_type IS NULL AND related_id IS NULL) OR (related_type IS NOT NULL AND length(related_id) >= 1)) AND length(target_id) >= 1 AND attributes LIKE '{%}');");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.operation_ledger ADD CONSTRAINT ck_operation_ledger_values CHECK (fingerprint ~ '^[0-9a-f]{64}$' AND status IN ('Applied', 'AlreadyInState', 'Rejected', 'Failed', 'Unsupported'));");
            migrationBuilder.Sql(@"CREATE FUNCTION provisioning.guard_provisioning_item() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'provisioning.provisioning_item rows cannot be deleted (DL-093)';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id
        OR NEW.approval_actions IS DISTINCT FROM OLD.approval_actions
        OR NEW.integrity_problem IS DISTINCT FROM OLD.integrity_problem
        OR NEW.beneficiary IS DISTINCT FROM OLD.beneficiary
        OR NEW.application_id IS DISTINCT FROM OLD.application_id
        OR NEW.entitlement_id IS DISTINCT FROM OLD.entitlement_id
        OR NEW.entitlement_version_id IS DISTINCT FROM OLD.entitlement_version_id
        OR NEW.account_id IS DISTINCT FROM OLD.account_id
        OR NEW.new_account_type IS DISTINCT FROM OLD.new_account_type
        OR NEW.account_name IS DISTINCT FROM OLD.account_name
        OR NEW.connector_key IS DISTINCT FROM OLD.connector_key
        OR NEW.received_at_utc IS DISTINCT FROM OLD.received_at_utc
        OR NEW.created_at_utc IS DISTINCT FROM OLD.created_at_utc
        OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
        RAISE EXCEPTION 'What a provisioning item was planned from never changes (DL-093)';
    END IF;
    IF NOT ((NEW.state = OLD.state AND NEW.problem IS NOT DISTINCT FROM OLD.problem)
        OR (OLD.state = 'Planned' AND NEW.state = 'Running' AND NEW.problem IS NULL)
        OR (OLD.state = 'Running' AND NEW.state IN ('Planned', 'Succeeded') AND NEW.problem IS NULL)
        OR (OLD.state = 'Running' AND NEW.state = 'Failed')) THEN
        RAISE EXCEPTION 'A provisioning item''s state only moves forward (DL-093)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION provisioning.guard_provisioning_operation() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'provisioning.provisioning_operation rows cannot be deleted (DL-093)';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id
        OR NEW.item_id IS DISTINCT FROM OLD.item_id
        OR NEW.step IS DISTINCT FROM OLD.step
        OR NEW.action IS DISTINCT FROM OLD.action
        OR NEW.target_type IS DISTINCT FROM OLD.target_type
        OR NEW.target_id IS DISTINCT FROM OLD.target_id
        OR NEW.related_type IS DISTINCT FROM OLD.related_type
        OR NEW.related_id IS DISTINCT FROM OLD.related_id
        OR NEW.attributes IS DISTINCT FROM OLD.attributes
        OR NEW.created_at_utc IS DISTINCT FROM OLD.created_at_utc
        OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
        RAISE EXCEPTION 'A planned operation never changes (DL-093)';
    END IF;
    IF NOT (NEW.state = OLD.state
        OR (OLD.state = 'Planned' AND NEW.state = 'Running')
        OR (OLD.state = 'Running' AND NEW.state IN ('Planned', 'Succeeded', 'Failed'))) THEN
        RAISE EXCEPTION 'An operation''s state only moves forward (DL-093)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION provisioning.reject_append_only_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'provisioning.% rows are append-only (DL-093)', TG_TABLE_NAME;
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION provisioning.guard_provisioning_item() FROM PUBLIC;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION provisioning.guard_provisioning_operation() FROM PUBLIC;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION provisioning.reject_append_only_change() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_provisioning_item_guarded BEFORE UPDATE OR DELETE ON provisioning.provisioning_item FOR EACH ROW EXECUTE FUNCTION provisioning.guard_provisioning_item();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_provisioning_item_no_truncate BEFORE TRUNCATE ON provisioning.provisioning_item FOR EACH STATEMENT EXECUTE FUNCTION provisioning.reject_append_only_change();");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item ENABLE ALWAYS TRIGGER tr_provisioning_item_guarded;");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item ENABLE ALWAYS TRIGGER tr_provisioning_item_no_truncate;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_provisioning_operation_guarded BEFORE UPDATE OR DELETE ON provisioning.provisioning_operation FOR EACH ROW EXECUTE FUNCTION provisioning.guard_provisioning_operation();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_provisioning_operation_no_truncate BEFORE TRUNCATE ON provisioning.provisioning_operation FOR EACH STATEMENT EXECUTE FUNCTION provisioning.reject_append_only_change();");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_operation ENABLE ALWAYS TRIGGER tr_provisioning_operation_guarded;");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_operation ENABLE ALWAYS TRIGGER tr_provisioning_operation_no_truncate;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_operation_ledger_append_only BEFORE UPDATE OR DELETE ON provisioning.operation_ledger FOR EACH ROW EXECUTE FUNCTION provisioning.reject_append_only_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_operation_ledger_no_truncate BEFORE TRUNCATE ON provisioning.operation_ledger FOR EACH STATEMENT EXECUTE FUNCTION provisioning.reject_append_only_change();");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.operation_ledger ENABLE ALWAYS TRIGGER tr_operation_ledger_append_only;");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.operation_ledger ENABLE ALWAYS TRIGGER tr_operation_ledger_no_truncate;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_inbox_message_append_only BEFORE UPDATE OR DELETE ON provisioning.inbox_message FOR EACH ROW EXECUTE FUNCTION provisioning.reject_append_only_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_inbox_message_no_truncate BEFORE TRUNCATE ON provisioning.inbox_message FOR EACH STATEMENT EXECUTE FUNCTION provisioning.reject_append_only_change();");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.inbox_message ENABLE ALWAYS TRIGGER tr_inbox_message_append_only;");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.inbox_message ENABLE ALWAYS TRIGGER tr_inbox_message_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the provisioning schema's guards; only valid on a database that holds no provisioning worth keeping (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_provisioning_item_no_truncate ON provisioning.provisioning_item;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_provisioning_item_guarded ON provisioning.provisioning_item;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_provisioning_operation_no_truncate ON provisioning.provisioning_operation;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_provisioning_operation_guarded ON provisioning.provisioning_operation;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_operation_ledger_no_truncate ON provisioning.operation_ledger;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_operation_ledger_append_only ON provisioning.operation_ledger;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_inbox_message_no_truncate ON provisioning.inbox_message;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_inbox_message_append_only ON provisioning.inbox_message;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS provisioning.reject_append_only_change();");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS provisioning.guard_provisioning_operation();");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS provisioning.guard_provisioning_item();");
            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "provisioning");

            migrationBuilder.DropTable(
                name: "operation_ledger",
                schema: "provisioning");

            migrationBuilder.DropTable(
                name: "provisioning_operation",
                schema: "provisioning");

            migrationBuilder.DropTable(
                name: "provisioning_item",
                schema: "provisioning");
        }
    }
}
