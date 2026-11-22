using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Account
{
    /// <inheritdoc />
    public partial class AccountTargetKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "target_id_key",
                schema: "account",
                table: "account",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true,
                collation: "C");

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_target_id_key_code",
                schema: "account",
                table: "account",
                sql: "\"target_id_key\" <> '' AND \"target_id_key\" = btrim(\"target_id_key\")");
            // DL-098, hand-written and reviewed (not generated). An account's target id is unique among the application's live
            // accounts ignoring case, on both engines (directories compare DNs that way); its name and target id never change after
            // it is recorded; a retired naming-rule template is compared exactly on SQL Server, as on PostgreSQL.
            migrationBuilder.Sql(@"UPDATE account.account SET target_id_key = lower(target_id) WHERE target_id IS NOT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE account.account ADD CONSTRAINT ck_account_target_id_key CHECK ((target_id IS NULL AND target_id_key IS NULL) OR (target_id IS NOT NULL AND target_id_key IS NOT NULL AND target_id_key = lower(target_id)));");
            migrationBuilder.Sql(@"DROP INDEX account.ux_account_live_target;");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_account_live_target_key ON account.account (application_id, target_id_key) WHERE target_id_key IS NOT NULL AND status <> 'Closed';");
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION account.guard_account() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
        RAISE EXCEPTION 'account.account rows cannot be deleted (DL-089)';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF (NEW.id, NEW.application_id, NEW.type, NEW.owner, NEW.origin, NEW.link_request_id, NEW.provisioning_item_id, NEW.name, NEW.name_key, NEW.target_id, NEW.target_id_key)
            IS DISTINCT FROM (OLD.id, OLD.application_id, OLD.type, OLD.owner, OLD.origin, OLD.link_request_id, OLD.provisioning_item_id, OLD.name, OLD.name_key, OLD.target_id, OLD.target_id_key) THEN
            RAISE EXCEPTION 'An account''s application, type, owner, origin, name and target id are immutable (DL-089, DL-098)';
        END IF;
        RETURN NEW;
    END IF;
    IF NOT ((NEW.origin = 'Linked' AND NEW.status = 'Active' AND NOT NEW.verified AND EXISTS (
        SELECT 1 FROM account.account_link_request r
        WHERE r.id = NEW.link_request_id AND r.state = 'Approved' AND r.application_id = NEW.application_id AND r.type = NEW.type
          AND r.name = NEW.name AND r.target_id = NEW.target_id AND r.owner = NEW.owner
        FOR SHARE))
        OR (NEW.origin = 'Created' AND NEW.status = 'Active' AND NEW.verified AND NEW.provisioning_item_id IS NOT NULL AND NEW.target_id IS NOT NULL)) THEN
        RAISE EXCEPTION 'An account is recorded only by an approved link request naming it exactly, unverified, or by provisioning once the target confirmed it (DL-089, DL-094)';
    END IF;
    RETURN NEW;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION account.guard_account() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
        RAISE EXCEPTION 'account.account rows cannot be deleted (DL-089)';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF (NEW.id, NEW.application_id, NEW.type, NEW.owner, NEW.origin, NEW.link_request_id, NEW.provisioning_item_id)
            IS DISTINCT FROM (OLD.id, OLD.application_id, OLD.type, OLD.owner, OLD.origin, OLD.link_request_id, OLD.provisioning_item_id) THEN
            RAISE EXCEPTION 'An account''s application, type, owner and origin are immutable (DL-089)';
        END IF;
        RETURN NEW;
    END IF;
    IF NOT ((NEW.origin = 'Linked' AND NEW.status = 'Active' AND NOT NEW.verified AND EXISTS (
        SELECT 1 FROM account.account_link_request r
        WHERE r.id = NEW.link_request_id AND r.state = 'Approved' AND r.application_id = NEW.application_id AND r.type = NEW.type
          AND r.name = NEW.name AND r.target_id = NEW.target_id AND r.owner = NEW.owner
        FOR SHARE))
        OR (NEW.origin = 'Created' AND NEW.status = 'Active' AND NEW.verified AND NEW.provisioning_item_id IS NOT NULL AND NEW.target_id IS NOT NULL)) THEN
        RAISE EXCEPTION 'An account is recorded only by an approved link request naming it exactly, unverified, or by provisioning once the target confirmed it (DL-089, DL-094)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"DROP INDEX account.ux_account_live_target_key;");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_account_live_target ON account.account (application_id, target_id) WHERE target_id IS NOT NULL AND status <> 'Closed';");
            migrationBuilder.Sql(@"ALTER TABLE account.account DROP CONSTRAINT ck_account_target_id_key;");
            migrationBuilder.DropCheckConstraint(
                name: "ck_account_target_id_key_code",
                schema: "account",
                table: "account");

            migrationBuilder.DropColumn(
                name: "target_id_key",
                schema: "account",
                table: "account");
        }
    }
}
