using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Workflow
{
    /// <inheritdoc />
    public partial class WorkflowOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "workflow",
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

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_dispatched_at_utc_occurred_at_utc",
                schema: "workflow",
                table: "outbox_message",
                columns: new[] { "dispatched_at_utc", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_idempotency_key",
                schema: "workflow",
                table: "outbox_message",
                column: "idempotency_key",
                unique: true);

            // DL-076: what a message says never changes, nothing is deleted, a sent message stays sent, attempts never go down.
            migrationBuilder.Sql(@"ALTER TABLE workflow.outbox_message ADD CONSTRAINT ck_outbox_message_numbers CHECK (schema_version BETWEEN 1 AND 1000 AND attempts >= 0 AND length(payload) > 1);");
            migrationBuilder.Sql(@"CREATE FUNCTION workflow.guard_outbox_message() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'workflow.outbox_message rows cannot be deleted (DL-076)';
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
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION workflow.guard_outbox_message() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_outbox_message_guarded BEFORE UPDATE OR DELETE ON workflow.outbox_message FOR EACH ROW EXECUTE FUNCTION workflow.guard_outbox_message();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_outbox_message_no_truncate BEFORE TRUNCATE ON workflow.outbox_message FOR EACH STATEMENT EXECUTE FUNCTION workflow.reject_workflow_truncate();");
            migrationBuilder.Sql(@"ALTER TABLE workflow.outbox_message ENABLE ALWAYS TRIGGER tr_outbox_message_guarded;");
            migrationBuilder.Sql(@"ALTER TABLE workflow.outbox_message ENABLE ALWAYS TRIGGER tr_outbox_message_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_outbox_message_no_truncate ON workflow.outbox_message;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_outbox_message_guarded ON workflow.outbox_message;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS workflow.guard_outbox_message();");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "workflow");
        }
    }
}
