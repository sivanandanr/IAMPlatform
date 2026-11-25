using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class ProvisioningUnsettled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "unsettled",
                schema: "provisioning",
                table: "provisioning_operation",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // DL-102: a step whose earlier attempt may have changed the target stays marked, so no login can make an unknown
            // outcome look settled.
            migrationBuilder.Sql(@"CREATE FUNCTION provisioning.keep_operation_unsettled() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF OLD.unsettled AND NOT NEW.unsettled THEN
        RAISE EXCEPTION 'A step''s unknown outcome is kept (DL-102)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION provisioning.keep_operation_unsettled() FROM PUBLIC;");
            migrationBuilder.Sql("CREATE TRIGGER tr_provisioning_operation_unsettled BEFORE UPDATE ON provisioning.provisioning_operation FOR EACH ROW EXECUTE FUNCTION provisioning.keep_operation_unsettled();");
            migrationBuilder.Sql("ALTER TABLE provisioning.provisioning_operation ENABLE ALWAYS TRIGGER tr_provisioning_operation_unsettled;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020): Down is for an empty development database; with data, recover by restore and a compensating
            // forward migration.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_provisioning_operation_unsettled ON provisioning.provisioning_operation;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS provisioning.keep_operation_unsettled();");
            migrationBuilder.DropColumn(
                name: "unsettled",
                schema: "provisioning",
                table: "provisioning_operation");
        }
    }
}
