using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Workflow
{
    /// <inheritdoc />
    public partial class WorkflowCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "workflow",
                table: "approval_instance",
                type: "character(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true,
                collation: "C");

            migrationBuilder.AddCheckConstraint(
                name: "ck_approval_instance_correlation_id_code",
                schema: "workflow",
                table: "approval_instance",
                sql: "\"correlation_id\" <> '' AND \"correlation_id\" = btrim(\"correlation_id\")");
            // DL-094, hand-written and reviewed (not generated). The correlation id is a W3C trace id, set when the approval starts and
            // never changed afterwards (a separate trigger, so the existing guard stays as reviewed).
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_instance ADD CONSTRAINT ck_approval_instance_correlation_id CHECK (correlation_id IS NULL OR correlation_id ~ '^[0-9a-f]{32}$');");
            migrationBuilder.Sql(@"CREATE FUNCTION workflow.keep_approval_correlation() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF NEW.correlation_id IS DISTINCT FROM OLD.correlation_id THEN
        RAISE EXCEPTION 'An approval instance''s correlation id never changes (DL-094)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION workflow.keep_approval_correlation() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_approval_instance_correlation BEFORE UPDATE ON workflow.approval_instance FOR EACH ROW EXECUTE FUNCTION workflow.keep_approval_correlation();");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_instance ENABLE ALWAYS TRIGGER tr_approval_instance_correlation;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_approval_instance_correlation ON workflow.approval_instance;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS workflow.keep_approval_correlation();");
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_instance DROP CONSTRAINT ck_approval_instance_correlation_id;");
            migrationBuilder.DropCheckConstraint(
                name: "ck_approval_instance_correlation_id_code",
                schema: "workflow",
                table: "approval_instance");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "workflow",
                table: "approval_instance");
        }
    }
}
