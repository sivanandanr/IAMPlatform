using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Administration
{
    /// <inheritdoc />
    public partial class AccessControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "configuration");

            migrationBuilder.CreateTable(
                name: "access_change_request",
                schema: "configuration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    payload = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    maker = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    proposed_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    proposed_against_version = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    checker = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    decision_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: true),
                    applied_as_version = table.Column<long>(type: "bigint", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_change_request", x => x.id);
                    table.CheckConstraint("ck_access_change_request_checker_code", "\"checker\" <> '' AND \"checker\" = btrim(\"checker\")");
                    table.CheckConstraint("ck_access_change_request_kind_code", "\"kind\" <> '' AND \"kind\" = btrim(\"kind\")");
                    table.CheckConstraint("ck_access_change_request_maker_code", "\"maker\" <> '' AND \"maker\" = btrim(\"maker\")");
                    table.CheckConstraint("ck_access_change_request_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                });

            migrationBuilder.CreateTable(
                name: "access_role",
                schema: "configuration",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    assignable = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_role", x => x.code);
                    table.CheckConstraint("ck_access_role_code_code", "\"code\" <> '' AND \"code\" = btrim(\"code\")");
                });

            migrationBuilder.CreateTable(
                name: "access_model_version",
                schema: "configuration",
                columns: table => new
                {
                    model_version = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    change_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model_hash = table.Column<string>(type: "character varying(67)", maxLength: 67, nullable: false, collation: "C"),
                    applied_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    applied_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_model_version", x => x.model_version);
                    table.CheckConstraint("ck_access_model_version_kind_code", "\"kind\" <> '' AND \"kind\" = btrim(\"kind\")");
                    table.CheckConstraint("ck_access_model_version_model_hash_code", "\"model_hash\" <> '' AND \"model_hash\" = btrim(\"model_hash\")");
                    table.ForeignKey(
                        name: "fk_access_model_version_access_change_request_change_request_id",
                        column: x => x.change_request_id,
                        principalSchema: "configuration",
                        principalTable: "access_change_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "access_incompatible_role_pair",
                schema: "configuration",
                columns: table => new
                {
                    first = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    second = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_incompatible_role_pair", x => new { x.first, x.second });
                    table.CheckConstraint("ck_access_incompatible_role_pair_first_code", "\"first\" <> '' AND \"first\" = btrim(\"first\")");
                    table.CheckConstraint("ck_access_incompatible_role_pair_second_code", "\"second\" <> '' AND \"second\" = btrim(\"second\")");
                    table.ForeignKey(
                        name: "fk_access_incompatible_role_pair_access_role_first",
                        column: x => x.first,
                        principalSchema: "configuration",
                        principalTable: "access_role",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_access_incompatible_role_pair_access_role_second",
                        column: x => x.second,
                        principalSchema: "configuration",
                        principalTable: "access_role",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "access_role_action",
                schema: "configuration",
                columns: table => new
                {
                    role_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    action_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_role_action", x => new { x.role_code, x.action_id });
                    table.CheckConstraint("ck_access_role_action_action_id_code", "\"action_id\" <> '' AND \"action_id\" = btrim(\"action_id\")");
                    table.CheckConstraint("ck_access_role_action_role_code_code", "\"role_code\" <> '' AND \"role_code\" = btrim(\"role_code\")");
                    table.ForeignKey(
                        name: "fk_access_role_action_access_role_role_code",
                        column: x => x.role_code,
                        principalSchema: "configuration",
                        principalTable: "access_role",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "access_role_assignment",
                schema: "configuration",
                columns: table => new
                {
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    role_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_role_assignment", x => new { x.subject, x.role_code });
                    table.CheckConstraint("ck_access_role_assignment_role_code_code", "\"role_code\" <> '' AND \"role_code\" = btrim(\"role_code\")");
                    table.CheckConstraint("ck_access_role_assignment_subject_code", "\"subject\" <> '' AND \"subject\" = btrim(\"subject\")");
                    table.ForeignKey(
                        name: "fk_access_role_assignment_access_role_role_code",
                        column: x => x.role_code,
                        principalSchema: "configuration",
                        principalTable: "access_role",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_access_change_request_state_proposed_at_utc",
                schema: "configuration",
                table: "access_change_request",
                columns: new[] { "state", "proposed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_access_incompatible_role_pair_second",
                schema: "configuration",
                table: "access_incompatible_role_pair",
                column: "second");

            migrationBuilder.CreateIndex(
                name: "ix_access_model_version_change_request_id",
                schema: "configuration",
                table: "access_model_version",
                column: "change_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_access_role_assignment_role_code",
                schema: "configuration",
                table: "access_role_assignment",
                column: "role_code");
            // DL-059 / P0-013 hand-written and reviewed (not generated). Trigger names match the HasTrigger declarations in
            // AdministrationDbContext (snake_case). Errors carry "immutable" or "proposed". The CHECKs repeat the domain's own
            // state rules, so a row written by SQL outside the application still can't describe an impossible change. Every
            // comparison has its own IS NOT NULL, as on SQL Server.
            migrationBuilder.Sql("ALTER TABLE configuration.access_change_request ADD CONSTRAINT ck_access_change_request_kind_known CHECK (kind IN ('AssignRole', 'RemoveAssignment', 'DefineRole', 'AddIncompatiblePair', 'RemoveIncompatiblePair'));");
            migrationBuilder.Sql("ALTER TABLE configuration.access_change_request ADD CONSTRAINT ck_access_change_request_payload_json CHECK (payload IS JSON OBJECT WITH UNIQUE KEYS);");
            migrationBuilder.Sql("ALTER TABLE configuration.access_change_request ADD CONSTRAINT ck_access_change_request_proposed_against_version CHECK (proposed_against_version >= 0);");
            migrationBuilder.Sql(@"ALTER TABLE configuration.access_change_request ADD CONSTRAINT ck_access_change_request_state CHECK (
    (state = 'Proposed' AND checker IS NULL AND decision_reason IS NULL AND decided_at_utc IS NULL AND applied_as_version IS NULL)
    OR (state = 'Withdrawn' AND checker IS NOT NULL AND checker = maker AND decided_at_utc IS NOT NULL AND applied_as_version IS NULL)
    OR (state = 'Rejected' AND checker IS NOT NULL AND checker <> maker AND decision_reason IS NOT NULL AND decided_at_utc IS NOT NULL AND applied_as_version IS NULL)
    OR (state = 'Approved' AND checker IS NOT NULL AND checker <> maker AND decided_at_utc IS NOT NULL AND applied_as_version IS NOT NULL AND applied_as_version > proposed_against_version));");

            // Exactly one bootstrap version (number 1, no change request); every later version names the change that made it.
            migrationBuilder.Sql(@"ALTER TABLE configuration.access_model_version ADD CONSTRAINT ck_access_model_version_kind CHECK (
    (kind = 'Bootstrap' AND model_version = 1 AND change_request_id IS NULL)
    OR (kind = 'Change' AND model_version > 1 AND change_request_id IS NOT NULL));");
            migrationBuilder.Sql("ALTER TABLE configuration.access_model_version ADD CONSTRAINT ck_access_model_version_model_hash_hex CHECK (model_hash ~ '^v1:[0-9a-f]{64}$');");

            // An approved request names the version it produced, and that version names it back: the pair must match.
            migrationBuilder.Sql("ALTER TABLE configuration.access_model_version ADD CONSTRAINT ak_access_model_version_model_version_change_request_id UNIQUE (model_version, change_request_id);");
            migrationBuilder.Sql("ALTER TABLE configuration.access_change_request ADD CONSTRAINT fk_access_change_request_access_model_version_applied FOREIGN KEY (applied_as_version, id) REFERENCES configuration.access_model_version (model_version, change_request_id);");

            migrationBuilder.Sql(@"CREATE FUNCTION configuration.reject_access_model_version_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'configuration.access_model_version rows are immutable (DL-059)';
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION configuration.require_proposed_access_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
DECLARE
    v_state text;
BEGIN
    IF NEW.change_request_id IS NOT NULL THEN
        -- FOR NO KEY UPDATE holds the request row until commit, so a concurrent decision waits instead of slipping in, and
        -- two approvals of one request queue here rather than deadlock (FOR SHARE let both in, then both wanted to update).
        SELECT state INTO v_state FROM configuration.access_change_request WHERE id = NEW.change_request_id FOR NO KEY UPDATE;
        IF v_state IS DISTINCT FROM 'Proposed' THEN
            RAISE EXCEPTION 'A model version can only be claimed for a proposed change request (DL-059)';
        END IF;
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION configuration.reject_decided_access_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'configuration.access_change_request rows are immutable and cannot be deleted (DL-059)';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.kind IS DISTINCT FROM OLD.kind OR NEW.payload IS DISTINCT FROM OLD.payload
        OR NEW.maker IS DISTINCT FROM OLD.maker OR NEW.reason IS DISTINCT FROM OLD.reason
        OR NEW.proposed_at_utc IS DISTINCT FROM OLD.proposed_at_utc OR NEW.proposed_against_version IS DISTINCT FROM OLD.proposed_against_version THEN
        RAISE EXCEPTION 'configuration.access_change_request proposal fields are immutable (DL-059)';
    END IF;
    IF OLD.state <> 'Proposed' THEN
        RAISE EXCEPTION 'Decided configuration.access_change_request rows are immutable (DL-059)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION configuration.reject_access_model_version_change(), configuration.require_proposed_access_change(), configuration.reject_decided_access_change() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_access_model_version_append_only BEFORE UPDATE OR DELETE ON configuration.access_model_version
FOR EACH ROW EXECUTE FUNCTION configuration.reject_access_model_version_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_access_model_version_no_truncate BEFORE TRUNCATE ON configuration.access_model_version
FOR EACH STATEMENT EXECUTE FUNCTION configuration.reject_access_model_version_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_access_model_version_claims_proposed BEFORE INSERT ON configuration.access_model_version
FOR EACH ROW EXECUTE FUNCTION configuration.require_proposed_access_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_access_change_request_decided_immutable BEFORE UPDATE OR DELETE ON configuration.access_change_request
FOR EACH ROW EXECUTE FUNCTION configuration.reject_decided_access_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_access_change_request_no_truncate BEFORE TRUNCATE ON configuration.access_change_request
FOR EACH STATEMENT EXECUTE FUNCTION configuration.reject_decided_access_change();");
            migrationBuilder.Sql("ALTER TABLE configuration.access_model_version ENABLE ALWAYS TRIGGER tr_access_model_version_append_only;");
            migrationBuilder.Sql("ALTER TABLE configuration.access_model_version ENABLE ALWAYS TRIGGER tr_access_model_version_no_truncate;");
            migrationBuilder.Sql("ALTER TABLE configuration.access_model_version ENABLE ALWAYS TRIGGER tr_access_model_version_claims_proposed;");
            migrationBuilder.Sql("ALTER TABLE configuration.access_change_request ENABLE ALWAYS TRIGGER tr_access_change_request_decided_immutable;");
            migrationBuilder.Sql("ALTER TABLE configuration.access_change_request ENABLE ALWAYS TRIGGER tr_access_change_request_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE configuration.access_change_request DROP CONSTRAINT fk_access_change_request_access_model_version_applied;");

            migrationBuilder.DropTable(
                name: "access_incompatible_role_pair",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "access_model_version",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "access_role_action",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "access_role_assignment",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "access_change_request",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "access_role",
                schema: "configuration");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS configuration.reject_access_model_version_change();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS configuration.require_proposed_access_change();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS configuration.reject_decided_access_change();");
        }
    }
}
