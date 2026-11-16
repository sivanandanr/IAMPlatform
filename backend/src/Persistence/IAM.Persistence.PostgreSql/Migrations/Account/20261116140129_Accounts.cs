using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Account
{
    /// <inheritdoc />
    public partial class Accounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "account");

            migrationBuilder.CreateTable(
                name: "account_link_request",
                schema: "account",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    name_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    target_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
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
                    table.PrimaryKey("pk_account_link_request", x => x.id);
                    table.CheckConstraint("ck_account_link_request_checker_code", "\"checker\" <> '' AND \"checker\" = btrim(\"checker\")");
                    table.CheckConstraint("ck_account_link_request_maker_code", "\"maker\" <> '' AND \"maker\" = btrim(\"maker\")");
                    table.CheckConstraint("ck_account_link_request_name_code", "\"name\" <> '' AND \"name\" = btrim(\"name\")");
                    table.CheckConstraint("ck_account_link_request_name_key_code", "\"name_key\" <> '' AND \"name_key\" = btrim(\"name_key\")");
                    table.CheckConstraint("ck_account_link_request_owner_code", "\"owner\" <> '' AND \"owner\" = btrim(\"owner\")");
                    table.CheckConstraint("ck_account_link_request_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.CheckConstraint("ck_account_link_request_type_code", "\"type\" <> '' AND \"type\" = btrim(\"type\")");
                });

            migrationBuilder.CreateTable(
                name: "naming_rule",
                schema: "account",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_naming_rule", x => x.id);
                    table.CheckConstraint("ck_naming_rule_account_type_code", "\"account_type\" <> '' AND \"account_type\" = btrim(\"account_type\")");
                });

            migrationBuilder.CreateTable(
                name: "account",
                schema: "account",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    name_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    target_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    link_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    verified = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account", x => x.id);
                    table.CheckConstraint("ck_account_name_code", "\"name\" <> '' AND \"name\" = btrim(\"name\")");
                    table.CheckConstraint("ck_account_name_key_code", "\"name_key\" <> '' AND \"name_key\" = btrim(\"name_key\")");
                    table.CheckConstraint("ck_account_origin_code", "\"origin\" <> '' AND \"origin\" = btrim(\"origin\")");
                    table.CheckConstraint("ck_account_owner_code", "\"owner\" <> '' AND \"owner\" = btrim(\"owner\")");
                    table.CheckConstraint("ck_account_status_code", "\"status\" <> '' AND \"status\" = btrim(\"status\")");
                    table.CheckConstraint("ck_account_type_code", "\"type\" <> '' AND \"type\" = btrim(\"type\")");
                    table.ForeignKey(
                        name: "fk_account_account_link_request_link_request_id",
                        column: x => x.link_request_id,
                        principalSchema: "account",
                        principalTable: "account_link_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "naming_rule_change_request",
                schema: "account",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    naming_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("pk_naming_rule_change_request", x => x.id);
                    table.CheckConstraint("ck_naming_rule_change_request_checker_code", "\"checker\" <> '' AND \"checker\" = btrim(\"checker\")");
                    table.CheckConstraint("ck_naming_rule_change_request_content_hash_code", "\"content_hash\" <> '' AND \"content_hash\" = btrim(\"content_hash\")");
                    table.CheckConstraint("ck_naming_rule_change_request_maker_code", "\"maker\" <> '' AND \"maker\" = btrim(\"maker\")");
                    table.CheckConstraint("ck_naming_rule_change_request_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.ForeignKey(
                        name: "fk_naming_rule_change_request_naming_rule_naming_rule_id",
                        column: x => x.naming_rule_id,
                        principalSchema: "account",
                        principalTable: "naming_rule",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "naming_rule_version",
                schema: "account",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    naming_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    template = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
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
                    table.PrimaryKey("pk_naming_rule_version", x => x.id);
                    table.CheckConstraint("ck_naming_rule_version_content_hash_code", "\"content_hash\" <> '' AND \"content_hash\" = btrim(\"content_hash\")");
                    table.CheckConstraint("ck_naming_rule_version_edited_by_code", "\"edited_by\" <> '' AND \"edited_by\" = btrim(\"edited_by\")");
                    table.CheckConstraint("ck_naming_rule_version_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.ForeignKey(
                        name: "fk_naming_rule_version_naming_rule_change_request_activated_by_re~",
                        column: x => x.activated_by_request_id,
                        principalSchema: "account",
                        principalTable: "naming_rule_change_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_naming_rule_version_naming_rule_naming_rule_id",
                        column: x => x.naming_rule_id,
                        principalSchema: "account",
                        principalTable: "naming_rule",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_application_id_owner",
                schema: "account",
                table: "account",
                columns: new[] { "application_id", "owner" });

            migrationBuilder.CreateIndex(
                name: "ix_account_link_request_id",
                schema: "account",
                table: "account",
                column: "link_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_link_request_state_proposed_at_utc",
                schema: "account",
                table: "account_link_request",
                columns: new[] { "state", "proposed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_naming_rule_application_id_account_type",
                schema: "account",
                table: "naming_rule",
                columns: new[] { "application_id", "account_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_naming_rule_change_request_naming_rule_id",
                schema: "account",
                table: "naming_rule_change_request",
                column: "naming_rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_naming_rule_change_request_state_proposed_at_utc",
                schema: "account",
                table: "naming_rule_change_request",
                columns: new[] { "state", "proposed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_naming_rule_change_request_version_id",
                schema: "account",
                table: "naming_rule_change_request",
                column: "version_id");

            migrationBuilder.CreateIndex(
                name: "ix_naming_rule_version_activated_by_request_id",
                schema: "account",
                table: "naming_rule_version",
                column: "activated_by_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_naming_rule_version_naming_rule_id_version_number",
                schema: "account",
                table: "naming_rule_version",
                columns: new[] { "naming_rule_id", "version_number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_naming_rule_change_request_naming_rule_version_version_id",
                schema: "account",
                table: "naming_rule_change_request",
                column: "version_id",
                principalSchema: "account",
                principalTable: "naming_rule_version",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            // DL-089, hand-written and reviewed (not generated). Accounts are never deleted; who an account belongs to, its type,
            // application and origin never change. In this slice an account is recorded only by an approved link request naming it
            // exactly, decided by someone other than the maker or the account's owner. One live Primary per person per
            // application; live names (ignoring case) and target ids are unique per application. Naming rule versions become
            // active only by an approved request naming them, decided by someone other than the content's author; active and
            // superseded versions and decided requests never change. Trigger names match the HasTrigger declarations in
            // AccountDbContext.
            migrationBuilder.Sql(@"ALTER TABLE account.account ADD CONSTRAINT ck_account_codes CHECK (type IN ('Primary', 'Secondary', 'Service', 'Shared') AND status IN ('Requested', 'Active', 'Disabled', 'Closed') AND origin IN ('Linked', 'Created'));");
            migrationBuilder.Sql(@"ALTER TABLE account.account ADD CONSTRAINT ck_account_name CHECK (name ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,255}$' AND name_key = lower(name));");
            migrationBuilder.Sql(@"ALTER TABLE account.account ADD CONSTRAINT ck_account_origin CHECK ((origin = 'Linked' AND link_request_id IS NOT NULL) OR (origin = 'Created' AND link_request_id IS NULL));");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_account_one_primary ON account.account (application_id, owner) WHERE type = 'Primary' AND status <> 'Closed';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_account_live_name ON account.account (application_id, name_key) WHERE status <> 'Closed';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_account_live_target ON account.account (application_id, target_id) WHERE target_id IS NOT NULL AND status <> 'Closed';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_account_link_request ON account.account (link_request_id) WHERE link_request_id IS NOT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE account.account_link_request ADD CONSTRAINT ck_account_link_request_state CHECK (
    (state = 'Proposed' AND checker IS NULL AND decision_reason IS NULL AND decided_at_utc IS NULL)
    OR (state = 'Withdrawn' AND checker IS NOT NULL AND checker = maker AND decided_at_utc IS NOT NULL)
    OR (state = 'Rejected' AND checker IS NOT NULL AND checker <> maker AND checker <> owner AND decision_reason IS NOT NULL AND decided_at_utc IS NOT NULL)
    OR (state = 'Approved' AND checker IS NOT NULL AND checker <> maker AND checker <> owner AND decided_at_utc IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE account.account_link_request ADD CONSTRAINT ck_account_link_request_codes CHECK (type IN ('Primary', 'Secondary', 'Service', 'Shared') AND name_key = lower(name));");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_account_link_request_one_pending_name ON account.account_link_request (application_id, name_key) WHERE state = 'Proposed';");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule ADD CONSTRAINT ck_naming_rule_account_type CHECK (account_type IN ('Primary', 'Secondary'));");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule_version ADD CONSTRAINT ck_naming_rule_version_state CHECK (
    (state = 'Draft' AND activated_by_request_id IS NULL)
    OR (state IN ('Active', 'Superseded') AND activated_by_request_id IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule_version ADD CONSTRAINT ck_naming_rule_version_content_hash_hex CHECK (content_hash ~ '^[0-9a-f]{64}$' AND version_number >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_naming_rule_version_one_draft ON account.naming_rule_version (naming_rule_id) WHERE state = 'Draft';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_naming_rule_version_one_active ON account.naming_rule_version (naming_rule_id) WHERE state = 'Active';");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule_change_request ADD CONSTRAINT ck_naming_rule_change_request_state CHECK (
    (state = 'Proposed' AND checker IS NULL AND decision_reason IS NULL AND decided_at_utc IS NULL)
    OR (state = 'Withdrawn' AND checker IS NOT NULL AND checker = maker AND decided_at_utc IS NOT NULL)
    OR (state = 'Rejected' AND checker IS NOT NULL AND checker <> maker AND decision_reason IS NOT NULL AND decided_at_utc IS NOT NULL)
    OR (state = 'Approved' AND checker IS NOT NULL AND checker <> maker AND decided_at_utc IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule_change_request ADD CONSTRAINT ck_naming_rule_change_request_content_hash_hex CHECK (content_hash ~ '^[0-9a-f]{64}$' AND version_number >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_naming_rule_change_request_one_pending ON account.naming_rule_change_request (naming_rule_id) WHERE state = 'Proposed';");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule_change_request ADD CONSTRAINT ak_naming_rule_change_request_id_version_id UNIQUE (id, version_id);");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule_version ADD CONSTRAINT fk_naming_rule_version_request_names FOREIGN KEY (activated_by_request_id, id) REFERENCES account.naming_rule_change_request (id, version_id);");
            migrationBuilder.Sql(@"CREATE FUNCTION account.guard_account() RETURNS trigger
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
            migrationBuilder.Sql(@"CREATE FUNCTION account.reject_decided_link_request_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'account.account_link_request rows are immutable and cannot be deleted (DL-089)';
    END IF;
    IF (NEW.id, NEW.application_id, NEW.type, NEW.name, NEW.name_key, NEW.target_id, NEW.owner, NEW.maker, NEW.reason, NEW.proposed_at_utc)
        IS DISTINCT FROM (OLD.id, OLD.application_id, OLD.type, OLD.name, OLD.name_key, OLD.target_id, OLD.owner, OLD.maker, OLD.reason, OLD.proposed_at_utc) THEN
        RAISE EXCEPTION 'account.account_link_request proposal fields are immutable (DL-089)';
    END IF;
    IF OLD.state <> 'Proposed' THEN
        RAISE EXCEPTION 'Decided account.account_link_request rows are immutable (DL-089)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION account.guard_naming_rule() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'account.naming_rule rows cannot be deleted (DL-089)';
    END IF;
    IF (NEW.id, NEW.application_id, NEW.account_type) IS DISTINCT FROM (OLD.id, OLD.application_id, OLD.account_type) THEN
        RAISE EXCEPTION 'A naming rule''s application and account type are immutable (DL-089)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION account.reject_non_draft_naming_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP = 'TRUNCATE' THEN
        RAISE EXCEPTION 'account.naming_rule_version rows cannot be truncated (DL-089)';
    END IF;
    IF TG_OP = 'UPDATE' AND (NEW.id, NEW.naming_rule_id, NEW.version_number) IS DISTINCT FROM (OLD.id, OLD.naming_rule_id, OLD.version_number) THEN
        RAISE EXCEPTION 'Naming rule version id, rule and number are immutable (DL-089)';
    END IF;
    IF TG_OP = 'DELETE' THEN
        IF OLD.state <> 'Draft' THEN
            RAISE EXCEPTION 'Active or superseded naming rule versions are immutable (DL-089)';
        END IF;
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.state <> 'Draft' THEN
        IF NOT (OLD.state = 'Active' AND NEW.state = 'Superseded'
            AND (NEW.template, NEW.content_hash, NEW.edited_by, NEW.activated_by_request_id) IS NOT DISTINCT FROM (OLD.template, OLD.content_hash, OLD.edited_by, OLD.activated_by_request_id)) THEN
            RAISE EXCEPTION 'Active or superseded naming rule versions are immutable; an active version can only be superseded (DL-089)';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.state = 'Draft' THEN
        RETURN NEW;
    END IF;
    IF NEW.state <> 'Active' OR NOT EXISTS (
        SELECT 1 FROM account.naming_rule_change_request r
        WHERE r.id = NEW.activated_by_request_id AND r.version_id = NEW.id AND r.state = 'Approved'
          AND r.naming_rule_id = NEW.naming_rule_id AND r.version_number = NEW.version_number AND r.content_hash = NEW.content_hash
          AND r.checker <> NEW.edited_by
        FOR SHARE) THEN
        RAISE EXCEPTION 'A naming rule version becomes active only by an approved request naming it, decided by someone who did not write it (DL-089)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION account.reject_decided_naming_request_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'account.naming_rule_change_request rows are immutable and cannot be deleted (DL-089)';
    END IF;
    IF (NEW.id, NEW.naming_rule_id, NEW.version_id, NEW.version_number, NEW.content_hash, NEW.maker, NEW.reason, NEW.proposed_at_utc)
        IS DISTINCT FROM (OLD.id, OLD.naming_rule_id, OLD.version_id, OLD.version_number, OLD.content_hash, OLD.maker, OLD.reason, OLD.proposed_at_utc) THEN
        RAISE EXCEPTION 'account.naming_rule_change_request proposal fields are immutable (DL-089)';
    END IF;
    IF OLD.state <> 'Proposed' THEN
        RAISE EXCEPTION 'Decided account.naming_rule_change_request rows are immutable (DL-089)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION account.guard_account(), account.reject_decided_link_request_change(), account.guard_naming_rule(), account.reject_non_draft_naming_change(), account.reject_decided_naming_request_change() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_account_guarded BEFORE INSERT OR UPDATE OR DELETE ON account.account FOR EACH ROW EXECUTE FUNCTION account.guard_account();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_account_no_truncate BEFORE TRUNCATE ON account.account FOR EACH STATEMENT EXECUTE FUNCTION account.guard_account();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_account_link_request_decided_immutable BEFORE UPDATE OR DELETE ON account.account_link_request FOR EACH ROW EXECUTE FUNCTION account.reject_decided_link_request_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_account_link_request_no_truncate BEFORE TRUNCATE ON account.account_link_request FOR EACH STATEMENT EXECUTE FUNCTION account.reject_decided_link_request_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_naming_rule_immutable BEFORE UPDATE OR DELETE ON account.naming_rule FOR EACH ROW EXECUTE FUNCTION account.guard_naming_rule();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_naming_rule_no_truncate BEFORE TRUNCATE ON account.naming_rule FOR EACH STATEMENT EXECUTE FUNCTION account.guard_naming_rule();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_naming_rule_version_immutable BEFORE INSERT OR UPDATE OR DELETE ON account.naming_rule_version FOR EACH ROW EXECUTE FUNCTION account.reject_non_draft_naming_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_naming_rule_version_no_truncate BEFORE TRUNCATE ON account.naming_rule_version FOR EACH STATEMENT EXECUTE FUNCTION account.reject_non_draft_naming_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_naming_rule_change_request_decided_immutable BEFORE UPDATE OR DELETE ON account.naming_rule_change_request FOR EACH ROW EXECUTE FUNCTION account.reject_decided_naming_request_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_naming_rule_change_request_no_truncate BEFORE TRUNCATE ON account.naming_rule_change_request FOR EACH STATEMENT EXECUTE FUNCTION account.reject_decided_naming_request_change();");
            migrationBuilder.Sql(@"ALTER TABLE account.account ENABLE ALWAYS TRIGGER tr_account_guarded;");
            migrationBuilder.Sql(@"ALTER TABLE account.account ENABLE ALWAYS TRIGGER tr_account_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE account.account_link_request ENABLE ALWAYS TRIGGER tr_account_link_request_decided_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE account.account_link_request ENABLE ALWAYS TRIGGER tr_account_link_request_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule ENABLE ALWAYS TRIGGER tr_naming_rule_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule ENABLE ALWAYS TRIGGER tr_naming_rule_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule_version ENABLE ALWAYS TRIGGER tr_naming_rule_version_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule_version ENABLE ALWAYS TRIGGER tr_naming_rule_version_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule_change_request ENABLE ALWAYS TRIGGER tr_naming_rule_change_request_decided_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule_change_request ENABLE ALWAYS TRIGGER tr_naming_rule_change_request_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the account schema's guards; only valid on a database that holds no account data worth keeping (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_naming_rule_change_request_no_truncate ON account.naming_rule_change_request;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_naming_rule_change_request_decided_immutable ON account.naming_rule_change_request;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_naming_rule_version_no_truncate ON account.naming_rule_version;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_naming_rule_version_immutable ON account.naming_rule_version;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_naming_rule_no_truncate ON account.naming_rule;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_naming_rule_immutable ON account.naming_rule;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_account_link_request_no_truncate ON account.account_link_request;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_account_link_request_decided_immutable ON account.account_link_request;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_account_no_truncate ON account.account;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_account_guarded ON account.account;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS account.guard_account(), account.reject_decided_link_request_change(), account.guard_naming_rule(), account.reject_non_draft_naming_change(), account.reject_decided_naming_request_change();");
            migrationBuilder.Sql(@"ALTER TABLE account.naming_rule_version DROP CONSTRAINT fk_naming_rule_version_request_names;");
            migrationBuilder.DropForeignKey(
                name: "fk_naming_rule_change_request_naming_rule_naming_rule_id",
                schema: "account",
                table: "naming_rule_change_request");

            migrationBuilder.DropForeignKey(
                name: "fk_naming_rule_version_naming_rule_naming_rule_id",
                schema: "account",
                table: "naming_rule_version");

            migrationBuilder.DropForeignKey(
                name: "fk_naming_rule_change_request_naming_rule_version_version_id",
                schema: "account",
                table: "naming_rule_change_request");

            migrationBuilder.DropTable(
                name: "account",
                schema: "account");

            migrationBuilder.DropTable(
                name: "account_link_request",
                schema: "account");

            migrationBuilder.DropTable(
                name: "naming_rule",
                schema: "account");

            migrationBuilder.DropTable(
                name: "naming_rule_version",
                schema: "account");

            migrationBuilder.DropTable(
                name: "naming_rule_change_request",
                schema: "account");
        }
    }
}
