using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Identity
{
    /// <inheritdoc />
    public partial class AttributeCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "attribute_definition",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attribute_definition", x => x.id);
                    table.CheckConstraint("ck_attribute_definition_key_code", "\"key\" <> '' AND \"key\" = btrim(\"key\")");
                });

            migrationBuilder.CreateTable(
                name: "attribute_definition_version",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    attribute_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    data_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    subject_scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    source_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    sensitivity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    normalization = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    freshness_max_age_minutes = table.Column<int>(type: "integer", nullable: false),
                    max_length = table.Column<int>(type: "integer", nullable: true),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attribute_definition_version", x => x.id);
                    table.CheckConstraint("ck_attribute_definition_version_content_hash_code", "\"content_hash\" <> '' AND \"content_hash\" = btrim(\"content_hash\")");
                    table.CheckConstraint("ck_attribute_definition_version_data_type_code", "\"data_type\" <> '' AND \"data_type\" = btrim(\"data_type\")");
                    table.CheckConstraint("ck_attribute_definition_version_normalization_code", "\"normalization\" <> '' AND \"normalization\" = btrim(\"normalization\")");
                    table.CheckConstraint("ck_attribute_definition_version_sensitivity_code", "\"sensitivity\" <> '' AND \"sensitivity\" = btrim(\"sensitivity\")");
                    table.CheckConstraint("ck_attribute_definition_version_source_system_code", "\"source_system\" <> '' AND \"source_system\" = btrim(\"source_system\")");
                    table.CheckConstraint("ck_attribute_definition_version_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.CheckConstraint("ck_attribute_definition_version_subject_scope_code", "\"subject_scope\" <> '' AND \"subject_scope\" = btrim(\"subject_scope\")");
                    table.ForeignKey(
                        name: "fk_attribute_definition_version_attribute_definition_attribute~",
                        column: x => x.attribute_definition_id,
                        principalSchema: "identity",
                        principalTable: "attribute_definition",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attribute_definition_version_operator",
                schema: "identity",
                columns: table => new
                {
                    attribute_definition_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @operator = table.Column<string>(name: "operator", type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attribute_definition_version_operator", x => new { x.attribute_definition_version_id, x.@operator });
                    table.CheckConstraint("ck_attribute_definition_version_operator_operator_code", "\"operator\" <> '' AND \"operator\" = btrim(\"operator\")");
                    table.ForeignKey(
                        name: "fk_attribute_definition_version_operator_attribute_definition_~",
                        column: x => x.attribute_definition_version_id,
                        principalSchema: "identity",
                        principalTable: "attribute_definition_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attribute_definition_key",
                schema: "identity",
                table: "attribute_definition",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attribute_definition_version_attribute_definition_id_versio~",
                schema: "identity",
                table: "attribute_definition_version",
                columns: new[] { "attribute_definition_id", "version_number" },
                unique: true);

            // ADR-0015 / IAM-WF-019 immutability and the DL-007 publish lock, hand-written and reviewed (not generated).
            // Trigger names match the HasTrigger declarations in IdentityDbContext (snake_case). Errors carry "immutable".
            //
            // DL-007: until maker-checker exists, the database itself allows only Draft versions, so no login (including
            // the Identity app login with schema-wide DML) can publish by SQL. A reviewed migration lifts this constraint
            // together with the maker-checker workflow.
            migrationBuilder.Sql("ALTER TABLE identity.attribute_definition_version ADD CONSTRAINT ck_attribute_definition_version_state_draft_only CHECK (state = 'Draft');");

            // At most one draft and one published version per attribute; the reader and the domain rely on it.
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_attribute_definition_version_one_draft ON identity.attribute_definition_version (attribute_definition_id) WHERE state = 'Draft';");
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_attribute_definition_version_one_published ON identity.attribute_definition_version (attribute_definition_id) WHERE state = 'Published';");

            migrationBuilder.Sql(@"CREATE FUNCTION identity.reject_attribute_key_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF NEW.key IS DISTINCT FROM OLD.key OR NEW.id IS DISTINCT FROM OLD.id THEN
        RAISE EXCEPTION 'identity.attribute_definition key and id are immutable (ADR-0015)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION identity.reject_non_draft_version_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW.id IS DISTINCT FROM OLD.id
        OR NEW.attribute_definition_id IS DISTINCT FROM OLD.attribute_definition_id
        OR NEW.version_number IS DISTINCT FROM OLD.version_number) THEN
        RAISE EXCEPTION 'Attribute definition version id, definition and number are immutable (IAM-WF-019)';
    END IF;
    IF OLD.state <> 'Draft' THEN
        RAISE EXCEPTION 'Published or retired attribute definition versions are immutable (IAM-WF-019)';
    END IF;
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION identity.reject_non_draft_operator_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
DECLARE
    v_old_state text;
    v_new_state text;
BEGIN
    -- FOR SHARE locks the parent row, so a concurrent state change cannot slip between this check and commit.
    IF TG_OP IN ('UPDATE', 'DELETE') THEN
        SELECT state INTO v_old_state FROM identity.attribute_definition_version WHERE id = OLD.attribute_definition_version_id FOR SHARE;
    END IF;
    IF TG_OP IN ('INSERT', 'UPDATE') THEN
        SELECT state INTO v_new_state FROM identity.attribute_definition_version WHERE id = NEW.attribute_definition_version_id FOR SHARE;
    END IF;
    -- A missing parent means the draft version itself is being deleted (cascade); its own trigger already allowed that.
    IF (v_old_state IS NOT NULL AND v_old_state <> 'Draft') OR (v_new_state IS NOT NULL AND v_new_state <> 'Draft') THEN
        RAISE EXCEPTION 'Operators of published or retired attribute definition versions are immutable (IAM-WF-019)';
    END IF;
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION identity.reject_attribute_key_change(), identity.reject_non_draft_version_change(), identity.reject_non_draft_operator_change() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_attribute_definition_key_immutable BEFORE UPDATE ON identity.attribute_definition
FOR EACH ROW EXECUTE FUNCTION identity.reject_attribute_key_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_attribute_definition_version_immutable BEFORE UPDATE OR DELETE ON identity.attribute_definition_version
FOR EACH ROW EXECUTE FUNCTION identity.reject_non_draft_version_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_attribute_definition_version_operator_immutable BEFORE INSERT OR UPDATE OR DELETE ON identity.attribute_definition_version_operator
FOR EACH ROW EXECUTE FUNCTION identity.reject_non_draft_operator_change();");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_definition ENABLE ALWAYS TRIGGER tr_attribute_definition_key_immutable;");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_definition_version ENABLE ALWAYS TRIGGER tr_attribute_definition_version_immutable;");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_definition_version_operator ENABLE ALWAYS TRIGGER tr_attribute_definition_version_operator_immutable;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attribute_definition_version_operator",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "attribute_definition_version",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "attribute_definition",
                schema: "identity");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS identity.reject_attribute_key_change();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS identity.reject_non_draft_version_change();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS identity.reject_non_draft_operator_change();");
        }
    }
}
