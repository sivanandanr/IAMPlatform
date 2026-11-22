using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Policy
{
    /// <inheritdoc />
    public partial class MatrixGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "editors",
                schema: "policy",
                table: "matrix_version",
                type: "character varying(4100)",
                maxLength: 4100,
                nullable: true);
            // DL-098, hand-written and reviewed (not generated). A retired matrix version is compared exactly, as on PostgreSQL (the
            // document by bytes and length, not the database's case-insensitive collation), its editors included; and no one who
            // wrote any of the draft may be the checker who activates it.
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION policy.reject_non_draft_matrix_change() RETURNS trigger
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
            AND (NEW.document, NEW.content_hash, NEW.compiled_hash, NEW.edited_by, NEW.editors, NEW.activated_by_request_id)
                IS NOT DISTINCT FROM (OLD.document, OLD.content_hash, OLD.compiled_hash, OLD.edited_by, OLD.editors, OLD.activated_by_request_id)) THEN
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
          AND (NEW.editors IS NULL OR NOT (r.checker = ANY (string_to_array(NEW.editors, E'\n'))))
        FOR SHARE) THEN
        RAISE EXCEPTION 'A matrix version becomes active only by an approved request naming it, decided by someone who did not write it (DL-090)';
    END IF;
    RETURN NEW;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION policy.reject_non_draft_matrix_change() RETURNS trigger
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
            migrationBuilder.DropColumn(
                name: "editors",
                schema: "policy",
                table: "matrix_version");
        }
    }
}
