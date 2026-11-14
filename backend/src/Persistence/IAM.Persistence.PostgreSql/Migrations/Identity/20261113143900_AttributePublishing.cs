using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Identity
{
    /// <inheritdoc />
    public partial class AttributePublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "published_by_request_id",
                schema: "identity",
                table: "attribute_definition_version",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "attribute_publish_request",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    attribute_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attribute_definition_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    maker = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    proposed_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    checker = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    decision_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attribute_publish_request", x => x.id);
                    table.CheckConstraint("ck_attribute_publish_request_checker_code", "\"checker\" <> '' AND \"checker\" = btrim(\"checker\")");
                    table.CheckConstraint("ck_attribute_publish_request_content_hash_code", "\"content_hash\" <> '' AND \"content_hash\" = btrim(\"content_hash\")");
                    table.CheckConstraint("ck_attribute_publish_request_maker_code", "\"maker\" <> '' AND \"maker\" = btrim(\"maker\")");
                    table.CheckConstraint("ck_attribute_publish_request_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.ForeignKey(
                        name: "fk_attribute_publish_request_attribute_definition_attribute_de~",
                        column: x => x.attribute_definition_id,
                        principalSchema: "identity",
                        principalTable: "attribute_definition",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attribute_publish_request_attribute_definition_version_attr~",
                        column: x => x.attribute_definition_version_id,
                        principalSchema: "identity",
                        principalTable: "attribute_definition_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attribute_definition_version_published_by_request_id",
                schema: "identity",
                table: "attribute_definition_version",
                column: "published_by_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_attribute_publish_request_attribute_definition_id",
                schema: "identity",
                table: "attribute_publish_request",
                column: "attribute_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_attribute_publish_request_attribute_definition_version_id",
                schema: "identity",
                table: "attribute_publish_request",
                column: "attribute_definition_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_attribute_publish_request_state_proposed_at_utc",
                schema: "identity",
                table: "attribute_publish_request",
                columns: new[] { "state", "proposed_at_utc" });

            migrationBuilder.AddForeignKey(
                name: "fk_attribute_definition_version_attribute_publish_request_publis~",
                schema: "identity",
                table: "attribute_definition_version",
                column: "published_by_request_id",
                principalSchema: "identity",
                principalTable: "attribute_publish_request",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            // DL-007 / DL-064, hand-written and reviewed (not generated). Lifts the S1-08 draft-only lock: a version can now
            // be published, but only by an approved publish request that names it, and a published version can only be
            // retired with its content unchanged. Trigger names match the HasTrigger declarations in IdentityDbContext.
            migrationBuilder.Sql("ALTER TABLE identity.attribute_definition_version DROP CONSTRAINT ck_attribute_definition_version_state_draft_only;");
            migrationBuilder.Sql(@"ALTER TABLE identity.attribute_definition_version ADD CONSTRAINT ck_attribute_definition_version_state_published CHECK (
    (state = 'Draft' AND published_by_request_id IS NULL)
    OR (state IN ('Published', 'Retired') AND published_by_request_id IS NOT NULL));");

            migrationBuilder.Sql(@"ALTER TABLE identity.attribute_publish_request ADD CONSTRAINT ck_attribute_publish_request_state CHECK (
    (state = 'Proposed' AND checker IS NULL AND decision_reason IS NULL AND decided_at_utc IS NULL)
    OR (state = 'Withdrawn' AND checker IS NOT NULL AND checker = maker AND decided_at_utc IS NOT NULL)
    OR (state = 'Rejected' AND checker IS NOT NULL AND checker <> maker AND decision_reason IS NOT NULL AND decided_at_utc IS NOT NULL)
    OR (state = 'Approved' AND checker IS NOT NULL AND checker <> maker AND decided_at_utc IS NOT NULL));");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_publish_request ADD CONSTRAINT ck_attribute_publish_request_content_hash_hex CHECK (content_hash ~ '^[0-9a-f]{64}$');");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_publish_request ADD CONSTRAINT ck_attribute_publish_request_version_number CHECK (version_number >= 1);");
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_attribute_publish_request_one_pending ON identity.attribute_publish_request (attribute_definition_id) WHERE state = 'Proposed';");

            migrationBuilder.Sql("ALTER TABLE identity.attribute_publish_request ADD CONSTRAINT ak_attribute_publish_request_id_version_id UNIQUE (id, attribute_definition_version_id);");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_definition_version ADD CONSTRAINT fk_attribute_definition_version_publish_request_names FOREIGN KEY (published_by_request_id, id) REFERENCES identity.attribute_publish_request (id, attribute_definition_version_id);");

            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION identity.reject_non_draft_version_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW.id IS DISTINCT FROM OLD.id
        OR NEW.attribute_definition_id IS DISTINCT FROM OLD.attribute_definition_id
        OR NEW.version_number IS DISTINCT FROM OLD.version_number) THEN
        RAISE EXCEPTION 'Attribute definition version id, definition and number are immutable (IAM-WF-019)';
    END IF;
    IF TG_OP = 'DELETE' THEN
        IF OLD.state <> 'Draft' THEN
            RAISE EXCEPTION 'Published or retired attribute definition versions are immutable (IAM-WF-019)';
        END IF;
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.state <> 'Draft' THEN
        IF NOT (OLD.state = 'Published' AND NEW.state = 'Retired'
            AND (NEW.display_name, NEW.data_type, NEW.subject_scope, NEW.source_system, NEW.sensitivity, NEW.normalization,
                 NEW.freshness_max_age_minutes, NEW.max_length, NEW.content_hash, NEW.published_by_request_id)
                IS NOT DISTINCT FROM
                (OLD.display_name, OLD.data_type, OLD.subject_scope, OLD.source_system, OLD.sensitivity, OLD.normalization,
                 OLD.freshness_max_age_minutes, OLD.max_length, OLD.content_hash, OLD.published_by_request_id)) THEN
            RAISE EXCEPTION 'Published or retired attribute definition versions are immutable; a published version can only be retired (IAM-WF-019, DL-064)';
        END IF;
        RETURN NEW;
    END IF;
    -- A new row, or a draft changing state: anything but a draft needs an approved request naming this version.
    IF NEW.state <> 'Draft' AND (NEW.state <> 'Published' OR NOT EXISTS (
        SELECT 1 FROM identity.attribute_publish_request r
        WHERE r.id = NEW.published_by_request_id AND r.attribute_definition_version_id = NEW.id AND r.state = 'Approved'
          AND r.attribute_definition_id = NEW.attribute_definition_id AND r.version_number = NEW.version_number
          AND r.content_hash = NEW.content_hash
        FOR SHARE)) THEN
        RAISE EXCEPTION 'An attribute version can only be published by an approved publish request naming it (DL-007, DL-064)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql("DROP TRIGGER tr_attribute_definition_version_immutable ON identity.attribute_definition_version;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_attribute_definition_version_immutable BEFORE INSERT OR UPDATE OR DELETE ON identity.attribute_definition_version
FOR EACH ROW EXECUTE FUNCTION identity.reject_non_draft_version_change();");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_definition_version ENABLE ALWAYS TRIGGER tr_attribute_definition_version_immutable;");

            migrationBuilder.Sql(@"CREATE FUNCTION identity.reject_decided_publish_request_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'identity.attribute_publish_request rows are immutable and cannot be deleted (DL-064)';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.attribute_definition_id IS DISTINCT FROM OLD.attribute_definition_id
        OR NEW.attribute_definition_version_id IS DISTINCT FROM OLD.attribute_definition_version_id
        OR NEW.version_number IS DISTINCT FROM OLD.version_number OR NEW.content_hash IS DISTINCT FROM OLD.content_hash
        OR NEW.maker IS DISTINCT FROM OLD.maker OR NEW.reason IS DISTINCT FROM OLD.reason
        OR NEW.proposed_at_utc IS DISTINCT FROM OLD.proposed_at_utc THEN
        RAISE EXCEPTION 'identity.attribute_publish_request proposal fields are immutable (DL-064)';
    END IF;
    IF OLD.state <> 'Proposed' THEN
        RAISE EXCEPTION 'Decided identity.attribute_publish_request rows are immutable (DL-064)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION identity.reject_decided_publish_request_change() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_attribute_publish_request_decided_immutable BEFORE UPDATE OR DELETE ON identity.attribute_publish_request
FOR EACH ROW EXECUTE FUNCTION identity.reject_decided_publish_request_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_attribute_publish_request_no_truncate BEFORE TRUNCATE ON identity.attribute_publish_request
FOR EACH STATEMENT EXECUTE FUNCTION identity.reject_decided_publish_request_change();");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_publish_request ENABLE ALWAYS TRIGGER tr_attribute_publish_request_decided_immutable;");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_publish_request ENABLE ALWAYS TRIGGER tr_attribute_publish_request_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only valid while nothing has been published.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_attribute_publish_request_decided_immutable ON identity.attribute_publish_request;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_attribute_publish_request_no_truncate ON identity.attribute_publish_request;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS identity.reject_decided_publish_request_change();");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_definition_version DROP CONSTRAINT fk_attribute_definition_version_publish_request_names;");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_definition_version DROP CONSTRAINT ck_attribute_definition_version_state_published;");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_definition_version ADD CONSTRAINT ck_attribute_definition_version_state_draft_only CHECK (state = 'Draft');");
            migrationBuilder.Sql("DROP TRIGGER tr_attribute_definition_version_immutable ON identity.attribute_definition_version;");
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION identity.reject_non_draft_version_change() RETURNS trigger
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
            migrationBuilder.Sql(@"CREATE TRIGGER tr_attribute_definition_version_immutable BEFORE UPDATE OR DELETE ON identity.attribute_definition_version
FOR EACH ROW EXECUTE FUNCTION identity.reject_non_draft_version_change();");
            migrationBuilder.Sql("ALTER TABLE identity.attribute_definition_version ENABLE ALWAYS TRIGGER tr_attribute_definition_version_immutable;");

            migrationBuilder.DropForeignKey(
                name: "fk_attribute_definition_version_attribute_publish_request_publis~",
                schema: "identity",
                table: "attribute_definition_version");

            migrationBuilder.DropTable(
                name: "attribute_publish_request",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "ix_attribute_definition_version_published_by_request_id",
                schema: "identity",
                table: "attribute_definition_version");

            migrationBuilder.DropColumn(
                name: "published_by_request_id",
                schema: "identity",
                table: "attribute_definition_version");
        }
    }
}
