using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class ApplicationCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "application",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application", x => x.id);
                    table.CheckConstraint("ck_application_key_code", "\"key\" <> '' AND \"key\" = btrim(\"key\")");
                });

            migrationBuilder.CreateTable(
                name: "application_role_type",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    maker = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    proposed_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: true),
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
                    table.PrimaryKey("pk_application_role_type", x => x.id);
                    table.CheckConstraint("ck_application_role_type_checker_code", "\"checker\" <> '' AND \"checker\" = btrim(\"checker\")");
                    table.CheckConstraint("ck_application_role_type_code_code", "\"code\" <> '' AND \"code\" = btrim(\"code\")");
                    table.CheckConstraint("ck_application_role_type_maker_code", "\"maker\" <> '' AND \"maker\" = btrim(\"maker\")");
                    table.CheckConstraint("ck_application_role_type_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                });

            migrationBuilder.CreateTable(
                name: "application_settings_request",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    settings_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    proposed_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
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
                    table.PrimaryKey("pk_application_settings_request", x => x.id);
                    table.CheckConstraint("ck_application_settings_request_checker_code", "\"checker\" <> '' AND \"checker\" = btrim(\"checker\")");
                    table.CheckConstraint("ck_application_settings_request_content_hash_code", "\"content_hash\" <> '' AND \"content_hash\" = btrim(\"content_hash\")");
                    table.CheckConstraint("ck_application_settings_request_maker_code", "\"maker\" <> '' AND \"maker\" = btrim(\"maker\")");
                    table.CheckConstraint("ck_application_settings_request_proposed_owner_code", "\"proposed_owner\" <> '' AND \"proposed_owner\" = btrim(\"proposed_owner\")");
                    table.CheckConstraint("ck_application_settings_request_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.ForeignKey(
                        name: "fk_application_settings_request_application_application_id",
                        column: x => x.application_id,
                        principalSchema: "catalog",
                        principalTable: "application",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "application_settings_version",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    activated_by_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_settings_version", x => x.id);
                    table.CheckConstraint("ck_application_settings_version_content_hash_code", "\"content_hash\" <> '' AND \"content_hash\" = btrim(\"content_hash\")");
                    table.CheckConstraint("ck_application_settings_version_owner_code", "\"owner\" <> '' AND \"owner\" = btrim(\"owner\")");
                    table.CheckConstraint("ck_application_settings_version_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.ForeignKey(
                        name: "fk_application_settings_version_application_application_id",
                        column: x => x.application_id,
                        principalSchema: "catalog",
                        principalTable: "application",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_application_settings_version_application_settings_request_act~",
                        column: x => x.activated_by_request_id,
                        principalSchema: "catalog",
                        principalTable: "application_settings_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "application_settings_role",
                schema: "catalog",
                columns: table => new
                {
                    settings_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requirement = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    holder_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    holder_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_settings_role", x => new { x.settings_version_id, x.role_type_id });
                    table.CheckConstraint("ck_application_settings_role_holder_id_code", "\"holder_id\" <> '' AND \"holder_id\" = btrim(\"holder_id\")");
                    table.CheckConstraint("ck_application_settings_role_holder_kind_code", "\"holder_kind\" <> '' AND \"holder_kind\" = btrim(\"holder_kind\")");
                    table.CheckConstraint("ck_application_settings_role_requirement_code", "\"requirement\" <> '' AND \"requirement\" = btrim(\"requirement\")");
                    table.ForeignKey(
                        name: "fk_application_settings_role_application_role_type_role_type_id",
                        column: x => x.role_type_id,
                        principalSchema: "catalog",
                        principalTable: "application_role_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_application_settings_role_application_settings_version_settin~",
                        column: x => x.settings_version_id,
                        principalSchema: "catalog",
                        principalTable: "application_settings_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_application_key",
                schema: "catalog",
                table: "application",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_application_role_type_code",
                schema: "catalog",
                table: "application_role_type",
                column: "code");

            migrationBuilder.CreateIndex(
                name: "ix_application_settings_request_application_id",
                schema: "catalog",
                table: "application_settings_request",
                column: "application_id");

            migrationBuilder.CreateIndex(
                name: "ix_application_settings_request_settings_version_id",
                schema: "catalog",
                table: "application_settings_request",
                column: "settings_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_application_settings_request_state_proposed_at_utc",
                schema: "catalog",
                table: "application_settings_request",
                columns: new[] { "state", "proposed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_application_settings_role_role_type_id",
                schema: "catalog",
                table: "application_settings_role",
                column: "role_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_application_settings_version_activated_by_request_id",
                schema: "catalog",
                table: "application_settings_version",
                column: "activated_by_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_application_settings_version_application_id_version_number",
                schema: "catalog",
                table: "application_settings_version",
                columns: new[] { "application_id", "version_number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_application_settings_request_application_settings_version_s~",
                schema: "catalog",
                table: "application_settings_request",
                column: "settings_version_id",
                principalSchema: "catalog",
                principalTable: "application_settings_version",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            // DL-066, hand-written and reviewed (not generated). Built-in role types are seeded before the guards exist, so no
            // login can add another without a second person. Applications and role types are never deleted; a settings version
            // becomes active only by an approved request naming it, with every required role filled and only usable role
            // types; active and superseded versions and their roles never change, except an active version being superseded.
            // Trigger names match the HasTrigger declarations in CatalogDbContext.
            migrationBuilder.Sql(@"INSERT INTO catalog.application_role_type (id, code, display_name, state, created_at_utc, created_by, modified_at_utc, modified_by, version) VALUES ('0193a1f0-0000-7000-8000-000000000001', 'user_admin', 'User Admin', 'BuiltIn', '2026-11-14T00:00:00+00:00', 'migration:ApplicationCatalog', '2026-11-14T00:00:00+00:00', 'migration:ApplicationCatalog', 1), ('0193a1f0-0000-7000-8000-000000000002', 'it_admin', 'IT Admin', 'BuiltIn', '2026-11-14T00:00:00+00:00', 'migration:ApplicationCatalog', '2026-11-14T00:00:00+00:00', 'migration:ApplicationCatalog', 1), ('0193a1f0-0000-7000-8000-000000000003', 'technical_custodian', 'Technical custodian', 'BuiltIn', '2026-11-14T00:00:00+00:00', 'migration:ApplicationCatalog', '2026-11-14T00:00:00+00:00', 'migration:ApplicationCatalog', 1);");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application ADD CONSTRAINT ck_application_key_format CHECK (key ~ '^[a-z][a-z0-9_]{1,63}$');");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_role_type ADD CONSTRAINT ck_application_role_type_code_format CHECK (code ~ '^[a-z][a-z0-9_]{1,63}$');");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_role_type ADD CONSTRAINT ck_application_role_type_state CHECK (
    (state = 'BuiltIn' AND maker IS NULL AND reason IS NULL AND proposed_at_utc IS NULL AND checker IS NULL AND decision_reason IS NULL AND decided_at_utc IS NULL)
    OR (maker IS NOT NULL AND reason IS NOT NULL AND proposed_at_utc IS NOT NULL AND (
        (state = 'Proposed' AND checker IS NULL AND decision_reason IS NULL AND decided_at_utc IS NULL)
        OR (state = 'Withdrawn' AND checker IS NOT NULL AND checker = maker AND decided_at_utc IS NOT NULL)
        OR (state = 'Rejected' AND checker IS NOT NULL AND checker <> maker AND decision_reason IS NOT NULL AND decided_at_utc IS NOT NULL)
        OR (state = 'Approved' AND checker IS NOT NULL AND checker <> maker AND decided_at_utc IS NOT NULL))));");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_application_role_type_live_code ON catalog.application_role_type (code) WHERE state IN ('BuiltIn', 'Proposed', 'Approved');");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_version ADD CONSTRAINT ck_application_settings_version_state CHECK (
    (state = 'Draft' AND activated_by_request_id IS NULL)
    OR (state IN ('Active', 'Superseded') AND activated_by_request_id IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_version ADD CONSTRAINT ck_application_settings_version_content_hash_hex CHECK (content_hash ~ '^[0-9a-f]{64}$');");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_version ADD CONSTRAINT ck_application_settings_version_version_number CHECK (version_number >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_application_settings_version_one_draft ON catalog.application_settings_version (application_id) WHERE state = 'Draft';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_application_settings_version_one_active ON catalog.application_settings_version (application_id) WHERE state = 'Active';");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_role ADD CONSTRAINT ck_application_settings_role_requirement CHECK (requirement IN ('Optional', 'Required'));");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_role ADD CONSTRAINT ck_application_settings_role_holder CHECK (
    (holder_kind IS NULL AND holder_id IS NULL)
    OR (holder_kind IN ('Person', 'Group') AND holder_id IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_request ADD CONSTRAINT ck_application_settings_request_state CHECK (
    (state = 'Proposed' AND checker IS NULL AND decision_reason IS NULL AND decided_at_utc IS NULL)
    OR (state = 'Withdrawn' AND checker IS NOT NULL AND checker = maker AND decided_at_utc IS NOT NULL)
    OR (state = 'Rejected' AND checker IS NOT NULL AND checker <> maker AND checker <> proposed_owner AND decision_reason IS NOT NULL AND decided_at_utc IS NOT NULL)
    OR (state = 'Approved' AND checker IS NOT NULL AND checker <> maker AND checker <> proposed_owner AND decided_at_utc IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_request ADD CONSTRAINT ck_application_settings_request_content_hash_hex CHECK (content_hash ~ '^[0-9a-f]{64}$');");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_request ADD CONSTRAINT ck_application_settings_request_version_number CHECK (version_number >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_application_settings_request_one_pending ON catalog.application_settings_request (application_id) WHERE state = 'Proposed';");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_request ADD CONSTRAINT ak_application_settings_request_id_version_id UNIQUE (id, settings_version_id);");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_version ADD CONSTRAINT fk_application_settings_version_request_names FOREIGN KEY (activated_by_request_id, id) REFERENCES catalog.application_settings_request (id, settings_version_id);");
            migrationBuilder.Sql(@"CREATE FUNCTION catalog.reject_application_key_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'catalog.application rows cannot be deleted (DL-066)';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.key IS DISTINCT FROM OLD.key THEN
        RAISE EXCEPTION 'Application id and key are immutable (DL-066)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION catalog.guard_application_role_type() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
        RAISE EXCEPTION 'catalog.application_role_type rows cannot be deleted (DL-066)';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW.state <> 'Proposed' THEN
            RAISE EXCEPTION 'A new application role type must be proposed and approved by a second person (DL-066)';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.code IS DISTINCT FROM OLD.code OR NEW.display_name IS DISTINCT FROM OLD.display_name
        OR NEW.maker IS DISTINCT FROM OLD.maker OR NEW.reason IS DISTINCT FROM OLD.reason OR NEW.proposed_at_utc IS DISTINCT FROM OLD.proposed_at_utc THEN
        RAISE EXCEPTION 'Application role type proposal fields are immutable (DL-066)';
    END IF;
    IF OLD.state <> 'Proposed' THEN
        RAISE EXCEPTION 'Built-in and decided application role types are immutable (DL-066)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION catalog.reject_non_draft_settings_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW.id IS DISTINCT FROM OLD.id OR NEW.application_id IS DISTINCT FROM OLD.application_id
        OR NEW.version_number IS DISTINCT FROM OLD.version_number) THEN
        RAISE EXCEPTION 'Application settings version id, application and number are immutable (DL-066)';
    END IF;
    IF TG_OP = 'DELETE' THEN
        IF OLD.state <> 'Draft' THEN
            RAISE EXCEPTION 'Active or superseded application settings versions are immutable (DL-066)';
        END IF;
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.state <> 'Draft' THEN
        IF NOT (OLD.state = 'Active' AND NEW.state = 'Superseded'
            AND (NEW.owner, NEW.content_hash, NEW.activated_by_request_id) IS NOT DISTINCT FROM (OLD.owner, OLD.content_hash, OLD.activated_by_request_id)) THEN
            RAISE EXCEPTION 'Active or superseded application settings versions are immutable; an active version can only be superseded (DL-066)';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.state = 'Draft' THEN
        RETURN NEW;
    END IF;
    IF NEW.state <> 'Active' OR NOT EXISTS (
        SELECT 1 FROM catalog.application_settings_request r
        WHERE r.id = NEW.activated_by_request_id AND r.settings_version_id = NEW.id AND r.state = 'Approved'
          AND r.application_id = NEW.application_id AND r.version_number = NEW.version_number
          AND r.content_hash = NEW.content_hash AND r.proposed_owner = NEW.owner
        FOR SHARE) THEN
        RAISE EXCEPTION 'Application settings become active only by an approved request naming them (DL-066)';
    END IF;
    IF EXISTS (
        SELECT 1 FROM catalog.application_settings_role x
        WHERE x.settings_version_id = NEW.id AND x.requirement = 'Required' AND x.holder_id IS NULL) THEN
        RAISE EXCEPTION 'An application with an empty required role cannot be activated (AR-2.3, DL-052)';
    END IF;
    IF EXISTS (
        SELECT 1 FROM catalog.application_settings_role x
        JOIN catalog.application_role_type t ON t.id = x.role_type_id
        WHERE x.settings_version_id = NEW.id AND t.state NOT IN ('BuiltIn', 'Approved')) THEN
        RAISE EXCEPTION 'Only built-in or approved role types can be used in active application settings (DL-066)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION catalog.reject_non_draft_role_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF (TG_OP <> 'INSERT' AND EXISTS (SELECT 1 FROM catalog.application_settings_version v WHERE v.id = OLD.settings_version_id AND v.state <> 'Draft' FOR SHARE))
        OR (TG_OP <> 'DELETE' AND EXISTS (SELECT 1 FROM catalog.application_settings_version v WHERE v.id = NEW.settings_version_id AND v.state <> 'Draft' FOR SHARE)) THEN
        RAISE EXCEPTION 'Roles of an active or superseded application settings version are immutable (DL-066)';
    END IF;
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION catalog.reject_decided_settings_request_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'catalog.application_settings_request rows are immutable and cannot be deleted (DL-066)';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.application_id IS DISTINCT FROM OLD.application_id
        OR NEW.settings_version_id IS DISTINCT FROM OLD.settings_version_id OR NEW.version_number IS DISTINCT FROM OLD.version_number
        OR NEW.content_hash IS DISTINCT FROM OLD.content_hash OR NEW.proposed_owner IS DISTINCT FROM OLD.proposed_owner
        OR NEW.maker IS DISTINCT FROM OLD.maker OR NEW.reason IS DISTINCT FROM OLD.reason
        OR NEW.proposed_at_utc IS DISTINCT FROM OLD.proposed_at_utc THEN
        RAISE EXCEPTION 'catalog.application_settings_request proposal fields are immutable (DL-066)';
    END IF;
    IF OLD.state <> 'Proposed' THEN
        RAISE EXCEPTION 'Decided catalog.application_settings_request rows are immutable (DL-066)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION catalog.reject_application_key_change(), catalog.guard_application_role_type(), catalog.reject_non_draft_settings_change(), catalog.reject_non_draft_role_change(), catalog.reject_decided_settings_request_change() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_application_key_immutable BEFORE UPDATE OR DELETE ON catalog.application FOR EACH ROW EXECUTE FUNCTION catalog.reject_application_key_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_application_no_truncate BEFORE TRUNCATE ON catalog.application FOR EACH STATEMENT EXECUTE FUNCTION catalog.reject_application_key_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_application_role_type_guarded BEFORE INSERT OR UPDATE OR DELETE ON catalog.application_role_type FOR EACH ROW EXECUTE FUNCTION catalog.guard_application_role_type();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_application_role_type_no_truncate BEFORE TRUNCATE ON catalog.application_role_type FOR EACH STATEMENT EXECUTE FUNCTION catalog.guard_application_role_type();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_application_settings_version_immutable BEFORE INSERT OR UPDATE OR DELETE ON catalog.application_settings_version FOR EACH ROW EXECUTE FUNCTION catalog.reject_non_draft_settings_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_application_settings_role_draft_only BEFORE INSERT OR UPDATE OR DELETE ON catalog.application_settings_role FOR EACH ROW EXECUTE FUNCTION catalog.reject_non_draft_role_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_application_settings_request_decided_immutable BEFORE UPDATE OR DELETE ON catalog.application_settings_request FOR EACH ROW EXECUTE FUNCTION catalog.reject_decided_settings_request_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_application_settings_request_no_truncate BEFORE TRUNCATE ON catalog.application_settings_request FOR EACH STATEMENT EXECUTE FUNCTION catalog.reject_decided_settings_request_change();");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application ENABLE ALWAYS TRIGGER tr_application_key_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application ENABLE ALWAYS TRIGGER tr_application_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_role_type ENABLE ALWAYS TRIGGER tr_application_role_type_guarded;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_role_type ENABLE ALWAYS TRIGGER tr_application_role_type_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_version ENABLE ALWAYS TRIGGER tr_application_settings_version_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_role ENABLE ALWAYS TRIGGER tr_application_settings_role_draft_only;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_request ENABLE ALWAYS TRIGGER tr_application_settings_request_decided_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_request ENABLE ALWAYS TRIGGER tr_application_settings_request_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the catalog with its history; only valid on a database that holds no catalog data worth keeping (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_application_settings_request_no_truncate ON catalog.application_settings_request;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_application_settings_request_decided_immutable ON catalog.application_settings_request;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_application_settings_role_draft_only ON catalog.application_settings_role;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_application_settings_version_immutable ON catalog.application_settings_version;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_application_role_type_no_truncate ON catalog.application_role_type;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_application_role_type_guarded ON catalog.application_role_type;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_application_no_truncate ON catalog.application;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_application_key_immutable ON catalog.application;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS catalog.reject_application_key_change(), catalog.guard_application_role_type(), catalog.reject_non_draft_settings_change(), catalog.reject_non_draft_role_change(), catalog.reject_decided_settings_request_change();");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_version DROP CONSTRAINT fk_application_settings_version_request_names;");
            migrationBuilder.DropForeignKey(
                name: "fk_application_settings_request_application_application_id",
                schema: "catalog",
                table: "application_settings_request");

            migrationBuilder.DropForeignKey(
                name: "fk_application_settings_version_application_application_id",
                schema: "catalog",
                table: "application_settings_version");

            migrationBuilder.DropForeignKey(
                name: "fk_application_settings_request_application_settings_version_s~",
                schema: "catalog",
                table: "application_settings_request");

            migrationBuilder.DropTable(
                name: "application_settings_role",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "application_role_type",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "application",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "application_settings_version",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "application_settings_request",
                schema: "catalog");
        }
    }
}
