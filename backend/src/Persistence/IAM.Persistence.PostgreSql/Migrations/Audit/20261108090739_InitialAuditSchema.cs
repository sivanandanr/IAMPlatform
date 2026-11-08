using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Audit
{
    /// <inheritdoc />
    public partial class InitialAuditSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "audit_event",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    effective_actor_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    target_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    target_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, collation: "C"),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    causation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    policy_version_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    policy_version_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true, collation: "C"),
                    payload_canonical = table.Column<string>(type: "text", nullable: false),
                    row_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    schema_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_event", x => new { x.recorded_at_utc, x.id });
                    table.CheckConstraint("ck_audit_event_action_code", "\"action\" <> '' AND \"action\" = btrim(\"action\")");
                    table.CheckConstraint("ck_audit_event_causation_id_code", "\"causation_id\" <> '' AND \"causation_id\" = btrim(\"causation_id\")");
                    table.CheckConstraint("ck_audit_event_correlation_id_code", "\"correlation_id\" <> '' AND \"correlation_id\" = btrim(\"correlation_id\")");
                    table.CheckConstraint("ck_audit_event_outcome_code", "\"outcome\" <> '' AND \"outcome\" = btrim(\"outcome\")");
                    table.CheckConstraint("ck_audit_event_policy_version_hash_code", "\"policy_version_hash\" <> '' AND \"policy_version_hash\" = btrim(\"policy_version_hash\")");
                    table.CheckConstraint("ck_audit_event_row_hash_code", "\"row_hash\" <> '' AND \"row_hash\" = btrim(\"row_hash\")");
                    table.CheckConstraint("ck_audit_event_target_type_code", "\"target_type\" <> '' AND \"target_type\" = btrim(\"target_type\")");
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_event_correlation_id",
                schema: "audit",
                table: "audit_event",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_event_id",
                schema: "audit",
                table: "audit_event",
                column: "id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_event_seq",
                schema: "audit",
                table: "audit_event",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_event_target_type_target_id",
                schema: "audit",
                table: "audit_event",
                columns: new[] { "target_type", "target_id" });

            // ADR-0018 append-only enforcement, hand-written and reviewed (not generated). Requires roles
            // iam_audit_writer and iam_audit_reader from infra/db/postgresql/grant-audit-role.sql; fails closed without them.
            // Grants are on this table only, never schema-wide, so the migration history stays migrator-only. Writers may
            // insert every column except seq, so they cannot choose sequence numbers.
            migrationBuilder.Sql("REVOKE ALL ON TABLE audit.audit_event FROM PUBLIC;");
            migrationBuilder.Sql("GRANT INSERT (id, recorded_at_utc, occurred_at_utc, actor_id, effective_actor_id, action, target_type, target_id, outcome, reason, correlation_id, causation_id, policy_version_ref, policy_version_hash, payload_canonical, row_hash, schema_version) ON TABLE audit.audit_event TO iam_audit_writer;");
            migrationBuilder.Sql("GRANT SELECT ON TABLE audit.audit_event TO iam_audit_reader;");

            // Second layer that also binds the schema owner: UPDATE, DELETE and TRUNCATE all fail with SQLSTATE P0001,
            // distinct from the 42501 the grants raise. ENABLE ALWAYS keeps the triggers firing even under
            // session_replication_role = replica.
            migrationBuilder.Sql(@"CREATE FUNCTION audit.reject_audit_event_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'audit.audit_event is append-only: % is not allowed (ADR-0018)', TG_OP;
END
$$;");
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION audit.reject_audit_event_change() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER audit_event_no_update_delete BEFORE UPDATE OR DELETE ON audit.audit_event
FOR EACH ROW EXECUTE FUNCTION audit.reject_audit_event_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER audit_event_no_truncate BEFORE TRUNCATE ON audit.audit_event
FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_audit_event_change();");
            migrationBuilder.Sql("ALTER TABLE audit.audit_event ENABLE ALWAYS TRIGGER audit_event_no_update_delete;");
            migrationBuilder.Sql("ALTER TABLE audit.audit_event ENABLE ALWAYS TRIGGER audit_event_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never drop the audit store: that would destroy evidence (ADR-0018). Roll back with a compensating forward
            // migration and, for data, restore/PITR (DL-020).
            throw new NotSupportedException("The audit schema cannot be rolled back by migration (ADR-0018).");
        }
    }
}
