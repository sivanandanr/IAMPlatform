using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Organization
{
    /// <inheritdoc />
    public partial class Workgroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "organization");

            migrationBuilder.CreateTable(
                name: "workgroup",
                schema: "organization",
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
                    table.PrimaryKey("pk_workgroup", x => x.id);
                    table.CheckConstraint("ck_workgroup_key_code", "\"key\" <> '' AND \"key\" = btrim(\"key\")");
                });

            migrationBuilder.CreateTable(
                name: "workgroup_change_request",
                schema: "organization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workgroup_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("pk_workgroup_change_request", x => x.id);
                    table.CheckConstraint("ck_workgroup_change_request_checker_code", "\"checker\" <> '' AND \"checker\" = btrim(\"checker\")");
                    table.CheckConstraint("ck_workgroup_change_request_content_hash_code", "\"content_hash\" <> '' AND \"content_hash\" = btrim(\"content_hash\")");
                    table.CheckConstraint("ck_workgroup_change_request_maker_code", "\"maker\" <> '' AND \"maker\" = btrim(\"maker\")");
                    table.CheckConstraint("ck_workgroup_change_request_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.ForeignKey(
                        name: "fk_workgroup_change_request_workgroup_workgroup_id",
                        column: x => x.workgroup_id,
                        principalSchema: "organization",
                        principalTable: "workgroup",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workgroup_version",
                schema: "organization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workgroup_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
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
                    table.PrimaryKey("pk_workgroup_version", x => x.id);
                    table.CheckConstraint("ck_workgroup_version_content_hash_code", "\"content_hash\" <> '' AND \"content_hash\" = btrim(\"content_hash\")");
                    table.CheckConstraint("ck_workgroup_version_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.ForeignKey(
                        name: "fk_workgroup_version_workgroup_change_request_activated_by_reque~",
                        column: x => x.activated_by_request_id,
                        principalSchema: "organization",
                        principalTable: "workgroup_change_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_workgroup_version_workgroup_workgroup_id",
                        column: x => x.workgroup_id,
                        principalSchema: "organization",
                        principalTable: "workgroup",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workgroup_member",
                schema: "organization",
                columns: table => new
                {
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workgroup_member", x => new { x.version_id, x.person });
                    table.CheckConstraint("ck_workgroup_member_person_code", "\"person\" <> '' AND \"person\" = btrim(\"person\")");
                    table.ForeignKey(
                        name: "fk_workgroup_member_workgroup_version_version_id",
                        column: x => x.version_id,
                        principalSchema: "organization",
                        principalTable: "workgroup_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workgroup_key",
                schema: "organization",
                table: "workgroup",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workgroup_change_request_state_proposed_at_utc",
                schema: "organization",
                table: "workgroup_change_request",
                columns: new[] { "state", "proposed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_workgroup_change_request_version_id",
                schema: "organization",
                table: "workgroup_change_request",
                column: "version_id");

            migrationBuilder.CreateIndex(
                name: "ix_workgroup_change_request_workgroup_id",
                schema: "organization",
                table: "workgroup_change_request",
                column: "workgroup_id");

            migrationBuilder.CreateIndex(
                name: "ix_workgroup_version_activated_by_request_id",
                schema: "organization",
                table: "workgroup_version",
                column: "activated_by_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_workgroup_version_workgroup_id_version_number",
                schema: "organization",
                table: "workgroup_version",
                columns: new[] { "workgroup_id", "version_number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_workgroup_change_request_workgroup_version_version_id",
                schema: "organization",
                table: "workgroup_change_request",
                column: "version_id",
                principalSchema: "organization",
                principalTable: "workgroup_version",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            // DL-068, hand-written and reviewed (not generated). The built-in fallback workgroup (iam_managers) is seeded with an
            // empty draft before the guards exist. Workgroups are never deleted; a member list becomes active only by an
            // approved request naming it, decided by someone who is not a member of it; active and superseded lists and
            // their members never change, except an active list being superseded. Trigger names match OrganizationDbContext.
            migrationBuilder.Sql(@"INSERT INTO organization.workgroup (id, key, display_name, created_at_utc, created_by, modified_at_utc, modified_by, version) VALUES ('0193a1f0-0000-7000-8000-0000000000f1', 'iam_managers', 'IAM managers', '2026-11-14T00:00:00+00:00', 'migration:Workgroups', '2026-11-14T00:00:00+00:00', 'migration:Workgroups', 1);");
            migrationBuilder.Sql(@"INSERT INTO organization.workgroup_version (id, workgroup_id, version_number, state, content_hash, activated_by_request_id, created_at_utc, created_by, modified_at_utc, modified_by, version) VALUES ('0193a1f0-0000-7000-8000-0000000000f2', '0193a1f0-0000-7000-8000-0000000000f1', 1, 'Draft', '66aca7e3a7e5de11db95a6b17e4e8c47bddcc5cbabda2e57fa3f61f82f5ea2a4', NULL, '2026-11-14T00:00:00+00:00', 'migration:Workgroups', '2026-11-14T00:00:00+00:00', 'migration:Workgroups', 1);");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup ADD CONSTRAINT ck_workgroup_key_format CHECK (key ~ '^[a-z][a-z0-9_]{1,63}$');");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_version ADD CONSTRAINT ck_workgroup_version_state CHECK (
    (state = 'Draft' AND activated_by_request_id IS NULL)
    OR (state IN ('Active', 'Superseded') AND activated_by_request_id IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_version ADD CONSTRAINT ck_workgroup_version_content_hash_hex CHECK (content_hash ~ '^[0-9a-f]{64}$');");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_version ADD CONSTRAINT ck_workgroup_version_version_number CHECK (version_number >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_workgroup_version_one_draft ON organization.workgroup_version (workgroup_id) WHERE state = 'Draft';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_workgroup_version_one_active ON organization.workgroup_version (workgroup_id) WHERE state = 'Active';");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_change_request ADD CONSTRAINT ck_workgroup_change_request_state CHECK (
    (state = 'Proposed' AND checker IS NULL AND decision_reason IS NULL AND decided_at_utc IS NULL)
    OR (state = 'Withdrawn' AND checker IS NOT NULL AND checker = maker AND decided_at_utc IS NOT NULL)
    OR (state = 'Rejected' AND checker IS NOT NULL AND checker <> maker AND decision_reason IS NOT NULL AND decided_at_utc IS NOT NULL)
    OR (state = 'Approved' AND checker IS NOT NULL AND checker <> maker AND decided_at_utc IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_change_request ADD CONSTRAINT ck_workgroup_change_request_content_hash_hex CHECK (content_hash ~ '^[0-9a-f]{64}$');");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_change_request ADD CONSTRAINT ck_workgroup_change_request_version_number CHECK (version_number >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_workgroup_change_request_one_pending ON organization.workgroup_change_request (workgroup_id) WHERE state = 'Proposed';");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_change_request ADD CONSTRAINT ak_workgroup_change_request_id_version_id UNIQUE (id, version_id);");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_version ADD CONSTRAINT fk_workgroup_version_request_names FOREIGN KEY (activated_by_request_id, id) REFERENCES organization.workgroup_change_request (id, version_id);");
            migrationBuilder.Sql(@"CREATE FUNCTION organization.reject_workgroup_key_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'organization.workgroup rows cannot be deleted (DL-068)';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.key IS DISTINCT FROM OLD.key THEN
        RAISE EXCEPTION 'Workgroup id and key are immutable (DL-068)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION organization.reject_non_draft_members_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW.id IS DISTINCT FROM OLD.id OR NEW.workgroup_id IS DISTINCT FROM OLD.workgroup_id
        OR NEW.version_number IS DISTINCT FROM OLD.version_number) THEN
        RAISE EXCEPTION 'Workgroup version id, workgroup and number are immutable (DL-068)';
    END IF;
    IF TG_OP = 'DELETE' THEN
        IF OLD.state <> 'Draft' THEN
            RAISE EXCEPTION 'Active or superseded workgroup member lists are immutable (DL-068)';
        END IF;
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.state <> 'Draft' THEN
        IF NOT (OLD.state = 'Active' AND NEW.state = 'Superseded'
            AND (NEW.content_hash, NEW.activated_by_request_id) IS NOT DISTINCT FROM (OLD.content_hash, OLD.activated_by_request_id)) THEN
            RAISE EXCEPTION 'Active or superseded workgroup member lists are immutable; an active list can only be superseded (DL-068)';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.state = 'Draft' THEN
        RETURN NEW;
    END IF;
    IF NEW.state <> 'Active' OR NOT EXISTS (
        SELECT 1 FROM organization.workgroup_change_request r
        WHERE r.id = NEW.activated_by_request_id AND r.version_id = NEW.id AND r.state = 'Approved'
          AND r.workgroup_id = NEW.workgroup_id AND r.version_number = NEW.version_number AND r.content_hash = NEW.content_hash
        FOR SHARE) THEN
        RAISE EXCEPTION 'Workgroup member lists become active only by an approved request naming them (DL-068)';
    END IF;
    IF EXISTS (
        SELECT 1 FROM organization.workgroup_member m
        JOIN organization.workgroup_change_request r ON r.id = NEW.activated_by_request_id
        WHERE m.version_id = NEW.id AND m.person = r.checker) THEN
        RAISE EXCEPTION 'A member of a workgroup member list cannot approve it (DL-068)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION organization.reject_non_draft_member_row_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF (TG_OP <> 'INSERT' AND EXISTS (SELECT 1 FROM organization.workgroup_version v WHERE v.id = OLD.version_id AND v.state <> 'Draft' FOR SHARE))
        OR (TG_OP <> 'DELETE' AND EXISTS (SELECT 1 FROM organization.workgroup_version v WHERE v.id = NEW.version_id AND v.state <> 'Draft' FOR SHARE)) THEN
        RAISE EXCEPTION 'Members of an active or superseded workgroup member list are immutable (DL-068)';
    END IF;
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION organization.reject_decided_workgroup_request_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'organization.workgroup_change_request rows are immutable and cannot be deleted (DL-068)';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.workgroup_id IS DISTINCT FROM OLD.workgroup_id
        OR NEW.version_id IS DISTINCT FROM OLD.version_id OR NEW.version_number IS DISTINCT FROM OLD.version_number
        OR NEW.content_hash IS DISTINCT FROM OLD.content_hash OR NEW.maker IS DISTINCT FROM OLD.maker
        OR NEW.reason IS DISTINCT FROM OLD.reason OR NEW.proposed_at_utc IS DISTINCT FROM OLD.proposed_at_utc THEN
        RAISE EXCEPTION 'organization.workgroup_change_request proposal fields are immutable (DL-068)';
    END IF;
    IF OLD.state <> 'Proposed' THEN
        RAISE EXCEPTION 'Decided organization.workgroup_change_request rows are immutable (DL-068)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION organization.reject_workgroup_truncate() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'Workgroup tables cannot be truncated (DL-068)';
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION organization.reject_workgroup_key_change(), organization.reject_non_draft_members_change(), organization.reject_non_draft_member_row_change(), organization.reject_decided_workgroup_request_change(), organization.reject_workgroup_truncate() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_workgroup_key_immutable BEFORE UPDATE OR DELETE ON organization.workgroup FOR EACH ROW EXECUTE FUNCTION organization.reject_workgroup_key_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_workgroup_version_immutable BEFORE INSERT OR UPDATE OR DELETE ON organization.workgroup_version FOR EACH ROW EXECUTE FUNCTION organization.reject_non_draft_members_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_workgroup_member_draft_only BEFORE INSERT OR UPDATE OR DELETE ON organization.workgroup_member FOR EACH ROW EXECUTE FUNCTION organization.reject_non_draft_member_row_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_workgroup_change_request_decided_immutable BEFORE UPDATE OR DELETE ON organization.workgroup_change_request FOR EACH ROW EXECUTE FUNCTION organization.reject_decided_workgroup_request_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_workgroup_no_truncate BEFORE TRUNCATE ON organization.workgroup FOR EACH STATEMENT EXECUTE FUNCTION organization.reject_workgroup_truncate();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_workgroup_version_no_truncate BEFORE TRUNCATE ON organization.workgroup_version FOR EACH STATEMENT EXECUTE FUNCTION organization.reject_workgroup_truncate();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_workgroup_member_no_truncate BEFORE TRUNCATE ON organization.workgroup_member FOR EACH STATEMENT EXECUTE FUNCTION organization.reject_workgroup_truncate();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_workgroup_change_request_no_truncate BEFORE TRUNCATE ON organization.workgroup_change_request FOR EACH STATEMENT EXECUTE FUNCTION organization.reject_workgroup_truncate();");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup ENABLE ALWAYS TRIGGER tr_workgroup_key_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_version ENABLE ALWAYS TRIGGER tr_workgroup_version_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_member ENABLE ALWAYS TRIGGER tr_workgroup_member_draft_only;");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_change_request ENABLE ALWAYS TRIGGER tr_workgroup_change_request_decided_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup ENABLE ALWAYS TRIGGER tr_workgroup_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_version ENABLE ALWAYS TRIGGER tr_workgroup_version_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_member ENABLE ALWAYS TRIGGER tr_workgroup_member_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_change_request ENABLE ALWAYS TRIGGER tr_workgroup_change_request_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the workgroups with their history; only valid where no workgroup data matters (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_workgroup_no_truncate ON organization.workgroup;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_workgroup_version_no_truncate ON organization.workgroup_version;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_workgroup_member_no_truncate ON organization.workgroup_member;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_workgroup_change_request_no_truncate ON organization.workgroup_change_request;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_workgroup_change_request_decided_immutable ON organization.workgroup_change_request;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_workgroup_member_draft_only ON organization.workgroup_member;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_workgroup_version_immutable ON organization.workgroup_version;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_workgroup_key_immutable ON organization.workgroup;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS organization.reject_workgroup_key_change(), organization.reject_non_draft_members_change(), organization.reject_non_draft_member_row_change(), organization.reject_decided_workgroup_request_change(), organization.reject_workgroup_truncate();");
            migrationBuilder.Sql(@"ALTER TABLE organization.workgroup_version DROP CONSTRAINT fk_workgroup_version_request_names;");
            migrationBuilder.DropForeignKey(
                name: "fk_workgroup_change_request_workgroup_version_version_id",
                schema: "organization",
                table: "workgroup_change_request");

            migrationBuilder.DropTable(
                name: "workgroup_member",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "workgroup_version",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "workgroup_change_request",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "workgroup",
                schema: "organization");
        }
    }
}
