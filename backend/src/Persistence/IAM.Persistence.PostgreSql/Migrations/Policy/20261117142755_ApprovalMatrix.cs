using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Policy
{
    /// <inheritdoc />
    public partial class ApprovalMatrix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "policy");

            migrationBuilder.CreateTable(
                name: "approval_matrix",
                schema: "policy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_matrix", x => x.id);
                    table.CheckConstraint("ck_approval_matrix_key_code", "\"key\" <> '' AND \"key\" = btrim(\"key\")");
                });

            migrationBuilder.CreateTable(
                name: "matrix_change_request",
                schema: "policy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    matrix_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    compiled_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
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
                    table.PrimaryKey("pk_matrix_change_request", x => x.id);
                    table.CheckConstraint("ck_matrix_change_request_checker_code", "\"checker\" <> '' AND \"checker\" = btrim(\"checker\")");
                    table.CheckConstraint("ck_matrix_change_request_compiled_hash_code", "\"compiled_hash\" <> '' AND \"compiled_hash\" = btrim(\"compiled_hash\")");
                    table.CheckConstraint("ck_matrix_change_request_content_hash_code", "\"content_hash\" <> '' AND \"content_hash\" = btrim(\"content_hash\")");
                    table.CheckConstraint("ck_matrix_change_request_maker_code", "\"maker\" <> '' AND \"maker\" = btrim(\"maker\")");
                    table.CheckConstraint("ck_matrix_change_request_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.ForeignKey(
                        name: "fk_matrix_change_request_approval_matrix_matrix_id",
                        column: x => x.matrix_id,
                        principalSchema: "policy",
                        principalTable: "approval_matrix",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "matrix_version",
                schema: "policy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    matrix_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    document = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    compiled_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    edited_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    activated_by_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_matrix_version", x => x.id);
                    table.CheckConstraint("ck_matrix_version_compiled_hash_code", "\"compiled_hash\" <> '' AND \"compiled_hash\" = btrim(\"compiled_hash\")");
                    table.CheckConstraint("ck_matrix_version_content_hash_code", "\"content_hash\" <> '' AND \"content_hash\" = btrim(\"content_hash\")");
                    table.CheckConstraint("ck_matrix_version_edited_by_code", "\"edited_by\" <> '' AND \"edited_by\" = btrim(\"edited_by\")");
                    table.CheckConstraint("ck_matrix_version_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.ForeignKey(
                        name: "fk_matrix_version_approval_matrix_matrix_id",
                        column: x => x.matrix_id,
                        principalSchema: "policy",
                        principalTable: "approval_matrix",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_matrix_version_matrix_change_request_activated_by_request_id",
                        column: x => x.activated_by_request_id,
                        principalSchema: "policy",
                        principalTable: "matrix_change_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approval_matrix_key",
                schema: "policy",
                table: "approval_matrix",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_matrix_change_request_matrix_id",
                schema: "policy",
                table: "matrix_change_request",
                column: "matrix_id");

            migrationBuilder.CreateIndex(
                name: "ix_matrix_change_request_state_proposed_at_utc",
                schema: "policy",
                table: "matrix_change_request",
                columns: new[] { "state", "proposed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_matrix_change_request_version_id",
                schema: "policy",
                table: "matrix_change_request",
                column: "version_id");

            migrationBuilder.CreateIndex(
                name: "ix_matrix_version_activated_by_request_id",
                schema: "policy",
                table: "matrix_version",
                column: "activated_by_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_matrix_version_matrix_id_version_number",
                schema: "policy",
                table: "matrix_version",
                columns: new[] { "matrix_id", "version_number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_matrix_change_request_matrix_version_version_id",
                schema: "policy",
                table: "matrix_change_request",
                column: "version_id",
                principalSchema: "policy",
                principalTable: "matrix_version",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            // DL-090, hand-written and reviewed (not generated). One enterprise matrix, never deleted. A matrix version becomes
            // active only by an approved request naming its exact content and compiled hash, decided by someone other than the
            // maker and the content's author; active and superseded versions and decided requests never change, except an active
            // version being superseded. Trigger names match the HasTrigger declarations in PolicyDbContext.
            migrationBuilder.Sql(@"ALTER TABLE policy.approval_matrix ADD CONSTRAINT ck_approval_matrix_key CHECK (key = 'enterprise');");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_version ADD CONSTRAINT ck_matrix_version_state CHECK (
    (state = 'Draft' AND activated_by_request_id IS NULL)
    OR (state IN ('Active', 'Superseded') AND activated_by_request_id IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_version ADD CONSTRAINT ck_matrix_version_hashes CHECK (content_hash ~ '^[0-9a-f]{64}$' AND compiled_hash ~ '^[0-9a-f]{64}$' AND version_number >= 1 AND octet_length(document) <= 8388608);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_matrix_version_one_draft ON policy.matrix_version (matrix_id) WHERE state = 'Draft';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_matrix_version_one_active ON policy.matrix_version (matrix_id) WHERE state = 'Active';");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_change_request ADD CONSTRAINT ck_matrix_change_request_state CHECK (
    (state = 'Proposed' AND checker IS NULL AND decision_reason IS NULL AND decided_at_utc IS NULL)
    OR (state = 'Withdrawn' AND checker IS NOT NULL AND checker = maker AND decided_at_utc IS NOT NULL)
    OR (state = 'Rejected' AND checker IS NOT NULL AND checker <> maker AND decision_reason IS NOT NULL AND decided_at_utc IS NOT NULL)
    OR (state = 'Approved' AND checker IS NOT NULL AND checker <> maker AND decided_at_utc IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_change_request ADD CONSTRAINT ck_matrix_change_request_hashes CHECK (content_hash ~ '^[0-9a-f]{64}$' AND compiled_hash ~ '^[0-9a-f]{64}$' AND version_number >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_matrix_change_request_one_pending ON policy.matrix_change_request (matrix_id) WHERE state = 'Proposed';");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_change_request ADD CONSTRAINT ak_matrix_change_request_id_version_id UNIQUE (id, version_id);");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_version ADD CONSTRAINT fk_matrix_version_request_names FOREIGN KEY (activated_by_request_id, id) REFERENCES policy.matrix_change_request (id, version_id);");
            migrationBuilder.Sql(@"CREATE FUNCTION policy.guard_approval_matrix() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'policy.approval_matrix rows cannot be deleted (DL-090)';
    END IF;
    IF (NEW.id, NEW.key) IS DISTINCT FROM (OLD.id, OLD.key) THEN
        RAISE EXCEPTION 'The approval matrix id and key are immutable (DL-090)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION policy.reject_non_draft_matrix_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP = 'TRUNCATE' THEN
        RAISE EXCEPTION 'policy.matrix_version rows cannot be truncated (DL-090)';
    END IF;
    IF TG_OP = 'UPDATE' AND (NEW.id, NEW.matrix_id, NEW.version_number) IS DISTINCT FROM (OLD.id, OLD.matrix_id, OLD.version_number) THEN
        RAISE EXCEPTION 'Matrix version id, matrix and number are immutable (DL-090)';
    END IF;
    IF TG_OP = 'DELETE' THEN
        IF OLD.state <> 'Draft' THEN
            RAISE EXCEPTION 'Active or superseded matrix versions are immutable (DL-090)';
        END IF;
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.state <> 'Draft' THEN
        IF NOT (OLD.state = 'Active' AND NEW.state = 'Superseded'
            AND (NEW.document, NEW.content_hash, NEW.compiled_hash, NEW.edited_by, NEW.activated_by_request_id)
                IS NOT DISTINCT FROM (OLD.document, OLD.content_hash, OLD.compiled_hash, OLD.edited_by, OLD.activated_by_request_id)) THEN
            RAISE EXCEPTION 'Active or superseded matrix versions are immutable; an active version can only be superseded (DL-090)';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.state = 'Draft' THEN
        RETURN NEW;
    END IF;
    IF NEW.state <> 'Active' OR NOT EXISTS (
        SELECT 1 FROM policy.matrix_change_request r
        WHERE r.id = NEW.activated_by_request_id AND r.version_id = NEW.id AND r.state = 'Approved'
          AND r.matrix_id = NEW.matrix_id AND r.version_number = NEW.version_number AND r.content_hash = NEW.content_hash
          AND r.compiled_hash = NEW.compiled_hash AND r.checker <> NEW.edited_by
        FOR SHARE) THEN
        RAISE EXCEPTION 'A matrix version becomes active only by an approved request naming it, decided by someone who did not write it (DL-090)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION policy.reject_decided_matrix_request_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'policy.matrix_change_request rows are immutable and cannot be deleted (DL-090)';
    END IF;
    IF (NEW.id, NEW.matrix_id, NEW.version_id, NEW.version_number, NEW.content_hash, NEW.compiled_hash, NEW.maker, NEW.reason, NEW.proposed_at_utc)
        IS DISTINCT FROM (OLD.id, OLD.matrix_id, OLD.version_id, OLD.version_number, OLD.content_hash, OLD.compiled_hash, OLD.maker, OLD.reason, OLD.proposed_at_utc) THEN
        RAISE EXCEPTION 'policy.matrix_change_request proposal fields are immutable (DL-090)';
    END IF;
    IF OLD.state <> 'Proposed' THEN
        RAISE EXCEPTION 'Decided policy.matrix_change_request rows are immutable (DL-090)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION policy.guard_approval_matrix(), policy.reject_non_draft_matrix_change(), policy.reject_decided_matrix_request_change() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_approval_matrix_immutable BEFORE UPDATE OR DELETE ON policy.approval_matrix FOR EACH ROW EXECUTE FUNCTION policy.guard_approval_matrix();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_approval_matrix_no_truncate BEFORE TRUNCATE ON policy.approval_matrix FOR EACH STATEMENT EXECUTE FUNCTION policy.guard_approval_matrix();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_matrix_version_immutable BEFORE INSERT OR UPDATE OR DELETE ON policy.matrix_version FOR EACH ROW EXECUTE FUNCTION policy.reject_non_draft_matrix_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_matrix_version_no_truncate BEFORE TRUNCATE ON policy.matrix_version FOR EACH STATEMENT EXECUTE FUNCTION policy.reject_non_draft_matrix_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_matrix_change_request_decided_immutable BEFORE UPDATE OR DELETE ON policy.matrix_change_request FOR EACH ROW EXECUTE FUNCTION policy.reject_decided_matrix_request_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_matrix_change_request_no_truncate BEFORE TRUNCATE ON policy.matrix_change_request FOR EACH STATEMENT EXECUTE FUNCTION policy.reject_decided_matrix_request_change();");
            migrationBuilder.Sql(@"ALTER TABLE policy.approval_matrix ENABLE ALWAYS TRIGGER tr_approval_matrix_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE policy.approval_matrix ENABLE ALWAYS TRIGGER tr_approval_matrix_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_version ENABLE ALWAYS TRIGGER tr_matrix_version_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_version ENABLE ALWAYS TRIGGER tr_matrix_version_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_change_request ENABLE ALWAYS TRIGGER tr_matrix_change_request_decided_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_change_request ENABLE ALWAYS TRIGGER tr_matrix_change_request_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the policy schema's guards; only valid on a database that holds no matrix worth keeping (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_matrix_change_request_no_truncate ON policy.matrix_change_request;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_matrix_change_request_decided_immutable ON policy.matrix_change_request;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_matrix_version_no_truncate ON policy.matrix_version;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_matrix_version_immutable ON policy.matrix_version;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_approval_matrix_no_truncate ON policy.approval_matrix;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_approval_matrix_immutable ON policy.approval_matrix;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS policy.guard_approval_matrix(), policy.reject_non_draft_matrix_change(), policy.reject_decided_matrix_request_change();");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_version DROP CONSTRAINT fk_matrix_version_request_names;");
            migrationBuilder.DropForeignKey(
                name: "fk_matrix_change_request_approval_matrix_matrix_id",
                schema: "policy",
                table: "matrix_change_request");

            migrationBuilder.DropForeignKey(
                name: "fk_matrix_version_approval_matrix_matrix_id",
                schema: "policy",
                table: "matrix_version");

            migrationBuilder.DropForeignKey(
                name: "fk_matrix_change_request_matrix_version_version_id",
                schema: "policy",
                table: "matrix_change_request");

            migrationBuilder.DropTable(
                name: "approval_matrix",
                schema: "policy");

            migrationBuilder.DropTable(
                name: "matrix_version",
                schema: "policy");

            migrationBuilder.DropTable(
                name: "matrix_change_request",
                schema: "policy");
        }
    }
}
