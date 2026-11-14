using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Workflow
{
    /// <inheritdoc />
    public partial class WorkflowStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "workflow");

            migrationBuilder.CreateTable(
                name: "approval_instance",
                schema: "workflow",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    beneficiary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    engine_version = table.Column<long>(type: "bigint", nullable: false),
                    action_count = table.Column<int>(type: "integer", nullable: false),
                    provisioning_authorized = table.Column<bool>(type: "boolean", nullable: false),
                    next_deadline_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_instance", x => x.id);
                    table.CheckConstraint("ck_approval_instance_beneficiary_code", "\"beneficiary\" <> '' AND \"beneficiary\" = btrim(\"beneficiary\")");
                    table.CheckConstraint("ck_approval_instance_requester_code", "\"requester\" <> '' AND \"requester\" = btrim(\"requester\")");
                    table.CheckConstraint("ck_approval_instance_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                });

            migrationBuilder.CreateTable(
                name: "approval_action",
                schema: "workflow",
                columns: table => new
                {
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    payload = table.Column<string>(type: "character varying(4000000)", maxLength: 4000000, nullable: false),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    version_after = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_action", x => new { x.instance_id, x.sequence });
                    table.CheckConstraint("ck_approval_action_actor_code", "\"actor\" <> '' AND \"actor\" = btrim(\"actor\")");
                    table.CheckConstraint("ck_approval_action_kind_code", "\"kind\" <> '' AND \"kind\" = btrim(\"kind\")");
                    table.ForeignKey(
                        name: "fk_approval_action_approval_instance_instance_id",
                        column: x => x.instance_id,
                        principalSchema: "workflow",
                        principalTable: "approval_instance",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "approval_history",
                schema: "workflow",
                columns: table => new
                {
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_version = table.Column<long>(type: "bigint", nullable: false),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false, collation: "C"),
                    stage_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_history", x => new { x.instance_id, x.event_version });
                    table.CheckConstraint("ck_approval_history_actor_code", "\"actor\" <> '' AND \"actor\" = btrim(\"actor\")");
                    table.CheckConstraint("ck_approval_history_kind_code", "\"kind\" <> '' AND \"kind\" = btrim(\"kind\")");
                    table.ForeignKey(
                        name: "fk_approval_history_approval_instance_instance_id",
                        column: x => x.instance_id,
                        principalSchema: "workflow",
                        principalTable: "approval_instance",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approval_instance_request_item_id",
                schema: "workflow",
                table: "approval_instance",
                column: "request_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_instance_state_next_deadline_utc",
                schema: "workflow",
                table: "approval_instance",
                columns: new[] { "state", "next_deadline_utc" });

            // Guards (DL-072a): append-only logs with no gaps, a summary that only moves forward, closed instances final.
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_instance ADD CONSTRAINT ck_approval_instance_state CHECK (state IN ('InProgress', 'Held', 'Approved', 'Rejected', 'Cancelled', 'Withdrawn', 'Returned'));");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_instance ADD CONSTRAINT ck_approval_instance_counts CHECK (engine_version >= 1 AND action_count >= 1);");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_action ADD CONSTRAINT ck_approval_action_kind CHECK (kind IN ('Bound', 'ApproversSupplied', 'Decided', 'Completed', 'Returned', 'Withdrawn', 'Cancelled', 'Escalated', 'EscalationProposed', 'EscalationApproved', 'EscalationWithdrawn', 'TimeoutsApplied'));");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_action ADD CONSTRAINT ck_approval_action_numbers CHECK (sequence >= 1 AND version_after >= 1 AND length(payload) > 0);");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_action ADD CONSTRAINT ck_approval_action_first_is_bound CHECK ((sequence = 1 AND kind = 'Bound') OR (sequence > 1 AND kind <> 'Bound'));");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_history ADD CONSTRAINT ck_approval_history_version CHECK (event_version >= 1);");
            migrationBuilder.Sql(@"CREATE FUNCTION workflow.guard_approval_action() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'workflow.approval_action rows are append-only (DL-072)';
    END IF;
    IF NEW.sequence > 1 AND NOT EXISTS (
            SELECT 1 FROM workflow.approval_action a
            WHERE a.instance_id = NEW.instance_id AND a.sequence = NEW.sequence - 1 AND a.version_after <= NEW.version_after
            FOR SHARE) THEN
        RAISE EXCEPTION 'Approval actions are numbered 1, 2, 3 with no gaps, and versions never go back (DL-072)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION workflow.guard_approval_history() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'workflow.approval_history rows are append-only (DL-072)';
    END IF;
    IF NEW.event_version > 1 AND NOT EXISTS (
            SELECT 1 FROM workflow.approval_history h
            WHERE h.instance_id = NEW.instance_id AND h.event_version = NEW.event_version - 1
            FOR SHARE) THEN
        RAISE EXCEPTION 'Approval history versions are 1, 2, 3 with no gaps (DL-072)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION workflow.guard_approval_instance() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'workflow.approval_instance rows cannot be deleted (DL-072)';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.request_item_id IS DISTINCT FROM OLD.request_item_id
        OR NEW.requester IS DISTINCT FROM OLD.requester OR NEW.beneficiary IS DISTINCT FROM OLD.beneficiary THEN
        RAISE EXCEPTION 'An approval instance''s id, request item and people are immutable (DL-072)';
    END IF;
    IF NEW.engine_version < OLD.engine_version OR NEW.action_count <= OLD.action_count THEN
        RAISE EXCEPTION 'An approval instance only moves forward: every save adds actions (DL-072)';
    END IF;
    IF OLD.state IN ('Approved', 'Rejected', 'Cancelled', 'Withdrawn', 'Returned') AND NEW.state IS DISTINCT FROM OLD.state THEN
        RAISE EXCEPTION 'A closed approval instance never changes state (DL-072)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION workflow.reject_workflow_truncate() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'Workflow tables cannot be truncated (DL-072)';
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION workflow.guard_approval_action(), workflow.guard_approval_history(), workflow.guard_approval_instance(), workflow.reject_workflow_truncate() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_approval_action_append_only BEFORE INSERT OR UPDATE OR DELETE ON workflow.approval_action FOR EACH ROW EXECUTE FUNCTION workflow.guard_approval_action();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_approval_history_append_only BEFORE INSERT OR UPDATE OR DELETE ON workflow.approval_history FOR EACH ROW EXECUTE FUNCTION workflow.guard_approval_history();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_approval_instance_guarded BEFORE UPDATE OR DELETE ON workflow.approval_instance FOR EACH ROW EXECUTE FUNCTION workflow.guard_approval_instance();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_approval_instance_no_truncate BEFORE TRUNCATE ON workflow.approval_instance FOR EACH STATEMENT EXECUTE FUNCTION workflow.reject_workflow_truncate();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_approval_action_no_truncate BEFORE TRUNCATE ON workflow.approval_action FOR EACH STATEMENT EXECUTE FUNCTION workflow.reject_workflow_truncate();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_approval_history_no_truncate BEFORE TRUNCATE ON workflow.approval_history FOR EACH STATEMENT EXECUTE FUNCTION workflow.reject_workflow_truncate();");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_action ENABLE ALWAYS TRIGGER tr_approval_action_append_only;");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_history ENABLE ALWAYS TRIGGER tr_approval_history_append_only;");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_instance ENABLE ALWAYS TRIGGER tr_approval_instance_guarded;");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_instance ENABLE ALWAYS TRIGGER tr_approval_instance_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_action ENABLE ALWAYS TRIGGER tr_approval_action_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_history ENABLE ALWAYS TRIGGER tr_approval_history_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_approval_history_no_truncate ON workflow.approval_history;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_approval_action_no_truncate ON workflow.approval_action;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_approval_instance_no_truncate ON workflow.approval_instance;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_approval_instance_guarded ON workflow.approval_instance;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_approval_history_append_only ON workflow.approval_history;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_approval_action_append_only ON workflow.approval_action;");

            migrationBuilder.DropTable(
                name: "approval_action",
                schema: "workflow");

            migrationBuilder.DropTable(
                name: "approval_history",
                schema: "workflow");

            migrationBuilder.DropTable(
                name: "approval_instance",
                schema: "workflow");

            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS workflow.guard_approval_action(), workflow.guard_approval_history(), workflow.guard_approval_instance(), workflow.reject_workflow_truncate();");
        }
    }
}
