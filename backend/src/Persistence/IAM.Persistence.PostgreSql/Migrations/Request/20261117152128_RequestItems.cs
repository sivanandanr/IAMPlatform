using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Request
{
    /// <inheritdoc />
    public partial class RequestItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "request");

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "request",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, collation: "C"),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    causation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    payload = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: true),
                    dispatched_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: true),
                    last_error = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_message", x => x.message_id);
                    table.CheckConstraint("ck_outbox_message_causation_id_code", "\"causation_id\" <> '' AND \"causation_id\" = btrim(\"causation_id\")");
                    table.CheckConstraint("ck_outbox_message_correlation_id_code", "\"correlation_id\" <> '' AND \"correlation_id\" = btrim(\"correlation_id\")");
                    table.CheckConstraint("ck_outbox_message_idempotency_key_code", "\"idempotency_key\" <> '' AND \"idempotency_key\" = btrim(\"idempotency_key\")");
                    table.CheckConstraint("ck_outbox_message_last_error_code", "\"last_error\" <> '' AND \"last_error\" = btrim(\"last_error\")");
                    table.CheckConstraint("ck_outbox_message_source_code", "\"source\" <> '' AND \"source\" = btrim(\"source\")");
                    table.CheckConstraint("ck_outbox_message_type_code", "\"type\" <> '' AND \"type\" = btrim(\"type\")");
                });

            migrationBuilder.CreateTable(
                name: "request_item",
                schema: "request",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    beneficiary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entitlement_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entitlement_content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_account_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    new_account_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true, collation: "C"),
                    duration_days = table.Column<int>(type: "integer", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    matrix_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    matrix_content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    matched_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_maker = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    evidence = table.Column<string>(type: "text", nullable: false),
                    route = table.Column<string>(type: "text", nullable: false),
                    submitted_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_request_item", x => x.id);
                    table.CheckConstraint("ck_request_item_beneficiary_code", "\"beneficiary\" <> '' AND \"beneficiary\" = btrim(\"beneficiary\")");
                    table.CheckConstraint("ck_request_item_entitlement_content_hash_code", "\"entitlement_content_hash\" <> '' AND \"entitlement_content_hash\" = btrim(\"entitlement_content_hash\")");
                    table.CheckConstraint("ck_request_item_matrix_content_hash_code", "\"matrix_content_hash\" <> '' AND \"matrix_content_hash\" = btrim(\"matrix_content_hash\")");
                    table.CheckConstraint("ck_request_item_new_account_name_code", "\"new_account_name\" <> '' AND \"new_account_name\" = btrim(\"new_account_name\")");
                    table.CheckConstraint("ck_request_item_new_account_type_code", "\"new_account_type\" <> '' AND \"new_account_type\" = btrim(\"new_account_type\")");
                    table.CheckConstraint("ck_request_item_policy_maker_code", "\"policy_maker\" <> '' AND \"policy_maker\" = btrim(\"policy_maker\")");
                    table.CheckConstraint("ck_request_item_requester_code", "\"requester\" <> '' AND \"requester\" = btrim(\"requester\")");
                    table.CheckConstraint("ck_request_item_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_dispatched_at_utc_occurred_at_utc",
                schema: "request",
                table: "outbox_message",
                columns: new[] { "dispatched_at_utc", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_idempotency_key",
                schema: "request",
                table: "outbox_message",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_request_item_beneficiary_submitted_at_utc",
                schema: "request",
                table: "request_item",
                columns: new[] { "beneficiary", "submitted_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_request_item_requester_submitted_at_utc",
                schema: "request",
                table: "request_item",
                columns: new[] { "requester", "submitted_at_utc" });
            // DL-091, hand-written and reviewed (not generated). A request item is frozen when submitted: nothing changes it and
            // nothing deletes it. One waiting item per person and entitlement. The outbox has the same guards as Workflow's
            // (DL-076, DL-079): what a message says never changes, nothing is deleted, a sent message stays sent.
            migrationBuilder.Sql(@"ALTER TABLE request.request_item ADD CONSTRAINT ck_request_item_shape CHECK (state IN ('Submitted') AND ((account_id IS NULL AND new_account_type IN ('Primary', 'Secondary', 'Service', 'Shared')) OR (account_id IS NOT NULL AND new_account_type IS NULL AND new_account_name IS NULL)) AND (duration_days IS NULL OR duration_days BETWEEN 1 AND 3650));");
            migrationBuilder.Sql(@"ALTER TABLE request.request_item ADD CONSTRAINT ck_request_item_hashes CHECK (entitlement_content_hash ~ '^[0-9a-f]{64}$' AND matrix_content_hash ~ '^[0-9a-f]{64}$');");
            migrationBuilder.Sql(@"ALTER TABLE request.request_item ADD CONSTRAINT ck_request_item_name CHECK (new_account_name IS NULL OR new_account_name ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,255}$');");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_request_item_one_waiting ON request.request_item (beneficiary, entitlement_id) WHERE state = 'Submitted';");
            migrationBuilder.Sql(@"CREATE FUNCTION request.reject_request_item_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'request.request_item rows are frozen when submitted: they cannot be changed or deleted (DL-091)';
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION request.reject_request_item_change() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_request_item_frozen BEFORE UPDATE OR DELETE ON request.request_item FOR EACH ROW EXECUTE FUNCTION request.reject_request_item_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_request_item_no_truncate BEFORE TRUNCATE ON request.request_item FOR EACH STATEMENT EXECUTE FUNCTION request.reject_request_item_change();");
            migrationBuilder.Sql(@"ALTER TABLE request.request_item ENABLE ALWAYS TRIGGER tr_request_item_frozen;");
            migrationBuilder.Sql(@"ALTER TABLE request.request_item ENABLE ALWAYS TRIGGER tr_request_item_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE request.outbox_message ADD CONSTRAINT ck_outbox_message_numbers CHECK (schema_version BETWEEN 1 AND 1000 AND attempts >= 0 AND length(payload) > 1);");
            migrationBuilder.Sql(@"CREATE FUNCTION request.guard_outbox_message() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'request.outbox_message rows cannot be deleted (DL-076)';
    END IF;
    IF NEW.message_id IS DISTINCT FROM OLD.message_id OR NEW.type IS DISTINCT FROM OLD.type
        OR NEW.schema_version IS DISTINCT FROM OLD.schema_version OR NEW.source IS DISTINCT FROM OLD.source
        OR NEW.correlation_id IS DISTINCT FROM OLD.correlation_id OR NEW.causation_id IS DISTINCT FROM OLD.causation_id
        OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key OR NEW.occurred_at_utc IS DISTINCT FROM OLD.occurred_at_utc
        OR NEW.payload IS DISTINCT FROM OLD.payload
        OR NEW.created_at_utc IS DISTINCT FROM OLD.created_at_utc OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
        RAISE EXCEPTION 'An outbox message''s content is immutable (DL-076)';
    END IF;
    IF OLD.dispatched_at_utc IS NOT NULL AND NEW.dispatched_at_utc IS DISTINCT FROM OLD.dispatched_at_utc THEN
        RAISE EXCEPTION 'A dispatched outbox message stays dispatched (DL-076)';
    END IF;
    IF NEW.attempts < OLD.attempts THEN
        RAISE EXCEPTION 'Outbox delivery attempts never go down (DL-076)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION request.guard_outbox_message() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_outbox_message_guarded BEFORE UPDATE OR DELETE ON request.outbox_message FOR EACH ROW EXECUTE FUNCTION request.guard_outbox_message();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_outbox_message_no_truncate BEFORE TRUNCATE ON request.outbox_message FOR EACH STATEMENT EXECUTE FUNCTION request.guard_outbox_message();");
            migrationBuilder.Sql(@"ALTER TABLE request.outbox_message ENABLE ALWAYS TRIGGER tr_outbox_message_guarded;");
            migrationBuilder.Sql(@"ALTER TABLE request.outbox_message ENABLE ALWAYS TRIGGER tr_outbox_message_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the request schema's guards; only valid on a database that holds no requests worth keeping (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_outbox_message_no_truncate ON request.outbox_message;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_outbox_message_guarded ON request.outbox_message;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS request.guard_outbox_message();");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_request_item_no_truncate ON request.request_item;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_request_item_frozen ON request.request_item;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS request.reject_request_item_change();");
            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "request");

            migrationBuilder.DropTable(
                name: "request_item",
                schema: "request");
        }
    }
}
