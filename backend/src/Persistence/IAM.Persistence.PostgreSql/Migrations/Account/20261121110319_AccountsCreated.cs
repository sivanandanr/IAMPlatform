using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Account
{
    /// <inheritdoc />
    public partial class AccountsCreated : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "provisioning_item_id",
                schema: "account",
                table: "account",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_account_provisioning_item_id",
                schema: "account",
                table: "account",
                column: "provisioning_item_id",
                unique: true);
            // DL-094, hand-written and reviewed (not generated). An account is recorded either by an approved link request (unverified,
            // as before) or by provisioning once the target confirmed it: origin Created, active, verified, from one provisioning item.
            // The database can't see the provisioning schema, so it checks the shape; who may write it is the account login (R-040).
            migrationBuilder.Sql(@"ALTER TABLE account.account DROP CONSTRAINT ck_account_origin;");
            migrationBuilder.Sql(@"ALTER TABLE account.account ADD CONSTRAINT ck_account_origin CHECK ((origin = 'Linked' AND link_request_id IS NOT NULL AND provisioning_item_id IS NULL) OR (origin = 'Created' AND link_request_id IS NULL AND provisioning_item_id IS NOT NULL));");
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
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION account.guard_account() FROM PUBLIC;");
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
        IF (NEW.id, NEW.application_id, NEW.type, NEW.owner, NEW.origin, NEW.link_request_id)
            IS DISTINCT FROM (OLD.id, OLD.application_id, OLD.type, OLD.owner, OLD.origin, OLD.link_request_id) THEN
            RAISE EXCEPTION 'An account''s application, type, owner and origin are immutable (DL-089)';
        END IF;
        RETURN NEW;
    END IF;
    IF NOT (NEW.origin = 'Linked' AND NEW.status = 'Active' AND NOT NEW.verified AND EXISTS (
        SELECT 1 FROM account.account_link_request r
        WHERE r.id = NEW.link_request_id AND r.state = 'Approved' AND r.application_id = NEW.application_id AND r.type = NEW.type
          AND r.name = NEW.name AND r.target_id = NEW.target_id AND r.owner = NEW.owner
        FOR SHARE)) THEN
        RAISE EXCEPTION 'An account is recorded only by an approved link request naming it exactly, unverified (DL-089)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"ALTER TABLE account.account DROP CONSTRAINT ck_account_origin;");
            migrationBuilder.Sql(@"ALTER TABLE account.account ADD CONSTRAINT ck_account_origin CHECK ((origin = 'Linked' AND link_request_id IS NOT NULL) OR (origin = 'Created' AND link_request_id IS NULL));");
            migrationBuilder.DropIndex(
                name: "ix_account_provisioning_item_id",
                schema: "account",
                table: "account");

            migrationBuilder.DropColumn(
                name: "provisioning_item_id",
                schema: "account",
                table: "account");
        }
    }
}
