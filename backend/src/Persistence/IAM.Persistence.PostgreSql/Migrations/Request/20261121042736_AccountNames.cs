using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Request
{
    /// <inheritdoc />
    public partial class AccountNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_name_assignment",
                schema: "request",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true, collation: "C"),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    set_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    set_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_name_assignment", x => x.id);
                    table.CheckConstraint("ck_account_name_assignment_name_code", "\"name\" <> '' AND \"name\" = btrim(\"name\")");
                    table.CheckConstraint("ck_account_name_assignment_previous_name_code", "\"previous_name\" <> '' AND \"previous_name\" = btrim(\"previous_name\")");
                    table.CheckConstraint("ck_account_name_assignment_set_by_code", "\"set_by\" <> '' AND \"set_by\" = btrim(\"set_by\")");
                    table.ForeignKey(
                        name: "fk_account_name_assignment_request_item_item_id",
                        column: x => x.item_id,
                        principalSchema: "request",
                        principalTable: "request_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_name_assignment_item_id_set_at_utc",
                schema: "request",
                table: "account_name_assignment",
                columns: new[] { "item_id", "set_at_utc" });
            // DL-092, hand-written and reviewed (not generated). A name a workflow level gave a new account is append-only: the
            // latest one is the name, and none is ever changed or deleted. Names are checked as accounts' are (hyphen first in the
            // SQL Server pattern, DL-091).
            migrationBuilder.Sql(@"ALTER TABLE request.account_name_assignment ADD CONSTRAINT ck_account_name_assignment_names CHECK (name ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,255}$' AND (previous_name IS NULL OR (previous_name ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,255}$' AND previous_name <> name)));");
            migrationBuilder.Sql(@"CREATE FUNCTION request.reject_account_name_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'request.account_name_assignment rows are append-only (DL-092)';
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION request.reject_account_name_change() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_account_name_assignment_append_only BEFORE UPDATE OR DELETE ON request.account_name_assignment FOR EACH ROW EXECUTE FUNCTION request.reject_account_name_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_account_name_assignment_no_truncate BEFORE TRUNCATE ON request.account_name_assignment FOR EACH STATEMENT EXECUTE FUNCTION request.reject_account_name_change();");
            migrationBuilder.Sql(@"ALTER TABLE request.account_name_assignment ENABLE ALWAYS TRIGGER tr_account_name_assignment_append_only;");
            migrationBuilder.Sql(@"ALTER TABLE request.account_name_assignment ENABLE ALWAYS TRIGGER tr_account_name_assignment_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_account_name_assignment_no_truncate ON request.account_name_assignment;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_account_name_assignment_append_only ON request.account_name_assignment;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS request.reject_account_name_change();");
            migrationBuilder.DropTable(
                name: "account_name_assignment",
                schema: "request");
        }
    }
}
