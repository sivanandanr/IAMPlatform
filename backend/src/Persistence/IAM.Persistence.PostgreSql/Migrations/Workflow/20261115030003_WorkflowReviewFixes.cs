using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Workflow
{
    /// <inheritdoc />
    public partial class WorkflowReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approval_damage",
                schema: "workflow",
                columns: table => new
                {
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    detected_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_damage", x => x.instance_id);
                    table.ForeignKey(
                        name: "fk_approval_damage_approval_instance_instance_id",
                        column: x => x.instance_id,
                        principalSchema: "workflow",
                        principalTable: "approval_instance",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // DL-075b: damage is recorded once and never changed. (The instance guard needs no change here: row triggers
            // don't fire for an UPDATE that matched nothing, DL-075h.)
            migrationBuilder.Sql(@"CREATE FUNCTION workflow.guard_approval_damage() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'workflow.approval_damage rows are append-only (DL-075)';
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION workflow.guard_approval_damage() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_approval_damage_append_only BEFORE UPDATE OR DELETE ON workflow.approval_damage FOR EACH ROW EXECUTE FUNCTION workflow.guard_approval_damage();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_approval_damage_no_truncate BEFORE TRUNCATE ON workflow.approval_damage FOR EACH STATEMENT EXECUTE FUNCTION workflow.reject_workflow_truncate();");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_damage ENABLE ALWAYS TRIGGER tr_approval_damage_append_only;");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_damage ENABLE ALWAYS TRIGGER tr_approval_damage_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_approval_damage_no_truncate ON workflow.approval_damage;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_approval_damage_append_only ON workflow.approval_damage;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS workflow.guard_approval_damage();");
            migrationBuilder.DropTable(
                name: "approval_damage",
                schema: "workflow");
        }
    }
}
