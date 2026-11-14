using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class ApplicationCatalogHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-067, hand-written and reviewed (not generated). Review fixes for the application catalog: a person named as
            // a role holder in the settings can't be the one who approved their activation; PostgreSQL also refuses TRUNCATE
            // of settings versions and their roles (SQL Server TRUNCATE never fires triggers and needs ALTER, which only the
            // schema owner has: recorded in R-034).
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION catalog.reject_non_draft_settings_change() RETURNS trigger
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
    IF EXISTS (
        SELECT 1 FROM catalog.application_settings_role x
        JOIN catalog.application_settings_request r ON r.id = NEW.activated_by_request_id
        WHERE x.settings_version_id = NEW.id AND x.holder_kind = 'Person' AND x.holder_id = r.checker) THEN
        RAISE EXCEPTION 'A person named as a role holder cannot approve those application settings (DL-067)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION catalog.reject_settings_truncate() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'Application settings versions and their roles cannot be truncated (DL-067)';
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION catalog.reject_settings_truncate() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_application_settings_version_no_truncate BEFORE TRUNCATE ON catalog.application_settings_version FOR EACH STATEMENT EXECUTE FUNCTION catalog.reject_settings_truncate();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_application_settings_role_no_truncate BEFORE TRUNCATE ON catalog.application_settings_role FOR EACH STATEMENT EXECUTE FUNCTION catalog.reject_settings_truncate();");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_version ENABLE ALWAYS TRIGGER tr_application_settings_version_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_role ENABLE ALWAYS TRIGGER tr_application_settings_role_no_truncate;");

            migrationBuilder.DropIndex(
                name: "ix_application_role_type_code",
                schema: "catalog",
                table: "application_role_type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores the ApplicationCatalog (DL-066) guards.
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_application_settings_role_no_truncate ON catalog.application_settings_role;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_application_settings_version_no_truncate ON catalog.application_settings_version;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS catalog.reject_settings_truncate();");
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION catalog.reject_non_draft_settings_change() RETURNS trigger
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

            migrationBuilder.CreateIndex(
                name: "ix_application_role_type_code",
                schema: "catalog",
                table: "application_role_type",
                column: "code");
        }
    }
}
