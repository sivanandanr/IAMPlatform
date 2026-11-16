using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class Entitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entitlement",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("pk_entitlement", x => x.id);
                    table.CheckConstraint("ck_entitlement_key_code", "\"key\" <> '' AND \"key\" = btrim(\"key\")");
                    table.ForeignKey(
                        name: "fk_entitlement_application_application_id",
                        column: x => x.application_id,
                        principalSchema: "catalog",
                        principalTable: "application",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "entitlement_change_request",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    owner_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    owner_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    accepted_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
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
                    table.PrimaryKey("pk_entitlement_change_request", x => x.id);
                    table.CheckConstraint("ck_entitlement_change_request_accepted_by_code", "\"accepted_by\" <> '' AND \"accepted_by\" = btrim(\"accepted_by\")");
                    table.CheckConstraint("ck_entitlement_change_request_checker_code", "\"checker\" <> '' AND \"checker\" = btrim(\"checker\")");
                    table.CheckConstraint("ck_entitlement_change_request_content_hash_code", "\"content_hash\" <> '' AND \"content_hash\" = btrim(\"content_hash\")");
                    table.CheckConstraint("ck_entitlement_change_request_maker_code", "\"maker\" <> '' AND \"maker\" = btrim(\"maker\")");
                    table.CheckConstraint("ck_entitlement_change_request_owner_id_code", "\"owner_id\" <> '' AND \"owner_id\" = btrim(\"owner_id\")");
                    table.CheckConstraint("ck_entitlement_change_request_owner_kind_code", "\"owner_kind\" <> '' AND \"owner_kind\" = btrim(\"owner_kind\")");
                    table.CheckConstraint("ck_entitlement_change_request_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.ForeignKey(
                        name: "fk_entitlement_change_request_entitlement_entitlement_id",
                        column: x => x.entitlement_id,
                        principalSchema: "catalog",
                        principalTable: "entitlement",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "entitlement_version",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    technical_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    owner_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    owner_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    escalation_workgroup = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    privileged = table.Column<bool>(type: "boolean", nullable: false),
                    risk = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    allowed_account_classes = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    access_for_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    access_for_days = table.Column<int>(type: "integer", nullable: true),
                    access_for_default_days = table.Column<int>(type: "integer", nullable: true),
                    access_for_max_days = table.Column<int>(type: "integer", nullable: true),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    edited_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    owner_accepted_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    owner_accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: true),
                    activated_by_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlement_version", x => x.id);
                    table.CheckConstraint("ck_entitlement_version_access_for_mode_code", "\"access_for_mode\" <> '' AND \"access_for_mode\" = btrim(\"access_for_mode\")");
                    table.CheckConstraint("ck_entitlement_version_allowed_account_classes_code", "\"allowed_account_classes\" <> '' AND \"allowed_account_classes\" = btrim(\"allowed_account_classes\")");
                    table.CheckConstraint("ck_entitlement_version_content_hash_code", "\"content_hash\" <> '' AND \"content_hash\" = btrim(\"content_hash\")");
                    table.CheckConstraint("ck_entitlement_version_edited_by_code", "\"edited_by\" <> '' AND \"edited_by\" = btrim(\"edited_by\")");
                    table.CheckConstraint("ck_entitlement_version_escalation_workgroup_code", "\"escalation_workgroup\" <> '' AND \"escalation_workgroup\" = btrim(\"escalation_workgroup\")");
                    table.CheckConstraint("ck_entitlement_version_owner_accepted_by_code", "\"owner_accepted_by\" <> '' AND \"owner_accepted_by\" = btrim(\"owner_accepted_by\")");
                    table.CheckConstraint("ck_entitlement_version_owner_id_code", "\"owner_id\" <> '' AND \"owner_id\" = btrim(\"owner_id\")");
                    table.CheckConstraint("ck_entitlement_version_owner_kind_code", "\"owner_kind\" <> '' AND \"owner_kind\" = btrim(\"owner_kind\")");
                    table.CheckConstraint("ck_entitlement_version_risk_code", "\"risk\" <> '' AND \"risk\" = btrim(\"risk\")");
                    table.CheckConstraint("ck_entitlement_version_state_code", "\"state\" <> '' AND \"state\" = btrim(\"state\")");
                    table.CheckConstraint("ck_entitlement_version_type_code", "\"type\" <> '' AND \"type\" = btrim(\"type\")");
                    table.ForeignKey(
                        name: "fk_entitlement_version_entitlement_change_request_activated_by_r~",
                        column: x => x.activated_by_request_id,
                        principalSchema: "catalog",
                        principalTable: "entitlement_change_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_entitlement_version_entitlement_entitlement_id",
                        column: x => x.entitlement_id,
                        principalSchema: "catalog",
                        principalTable: "entitlement",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_application_id_key",
                schema: "catalog",
                table: "entitlement",
                columns: new[] { "application_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_change_request_entitlement_id",
                schema: "catalog",
                table: "entitlement_change_request",
                column: "entitlement_id");

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_change_request_state_proposed_at_utc",
                schema: "catalog",
                table: "entitlement_change_request",
                columns: new[] { "state", "proposed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_change_request_version_id",
                schema: "catalog",
                table: "entitlement_change_request",
                column: "version_id");

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_version_activated_by_request_id",
                schema: "catalog",
                table: "entitlement_version",
                column: "activated_by_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_version_entitlement_id_version_number",
                schema: "catalog",
                table: "entitlement_version",
                columns: new[] { "entitlement_id", "version_number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_entitlement_change_request_entitlement_version_version_id",
                schema: "catalog",
                table: "entitlement_change_request",
                column: "version_id",
                principalSchema: "catalog",
                principalTable: "entitlement_version",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            // DL-087, hand-written and reviewed (not generated). Entitlements are never deleted and keep their id, application
            // and key. A version becomes active only by an approved request naming its exact content and owner, decided by
            // someone other than the person who wrote the content or accepted ownership; active and superseded versions never
            // change, except an active version being superseded. Privileged access never sits on a Primary account and always
            // has an end date; a Secondary account is only for privileged access (spec v3.2 §2.5, §3.1, §6.4).
            // Trigger names match the HasTrigger declarations in CatalogDbContext.
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement ADD CONSTRAINT ck_entitlement_key_format CHECK (key ~ '^[a-z][a-z0-9_]{1,63}$');");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_version ADD CONSTRAINT ck_entitlement_version_state CHECK (
    (state = 'Draft' AND activated_by_request_id IS NULL)
    OR (state IN ('Active', 'Superseded') AND activated_by_request_id IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_version ADD CONSTRAINT ck_entitlement_version_content_hash_hex CHECK (content_hash ~ '^[0-9a-f]{64}$');");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_version ADD CONSTRAINT ck_entitlement_version_version_number CHECK (version_number >= 1);");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_version ADD CONSTRAINT ck_entitlement_version_codes CHECK (type IN ('SecurityGroup', 'DistributionList', 'SharedMailbox') AND risk IN ('Low', 'Medium', 'High', 'Critical') AND owner_kind IN ('Person', 'Group'));");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_version ADD CONSTRAINT ck_entitlement_version_account_classes CHECK (allowed_account_classes IN ('Primary', 'Secondary', 'Primary,Secondary', 'Service', 'Primary,Service', 'Secondary,Service', 'Primary,Secondary,Service', 'Shared', 'Primary,Shared', 'Secondary,Shared', 'Primary,Secondary,Shared', 'Service,Shared', 'Primary,Service,Shared', 'Secondary,Service,Shared', 'Primary,Secondary,Service,Shared') AND ((privileged AND allowed_account_classes NOT LIKE '%Primary%') OR (NOT privileged AND allowed_account_classes NOT LIKE '%Secondary%')));");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_version ADD CONSTRAINT ck_entitlement_version_access_for CHECK (
    (access_for_mode = 'NoExpiry' AND NOT privileged AND access_for_days IS NULL AND access_for_default_days IS NULL AND access_for_max_days IS NULL)
    OR (access_for_mode = 'FixedDays' AND access_for_days BETWEEN 1 AND 3650 AND access_for_default_days IS NULL AND access_for_max_days IS NULL)
    OR (access_for_mode = 'RequesterChooses' AND access_for_days IS NULL AND access_for_default_days BETWEEN 1 AND 3650 AND access_for_max_days BETWEEN 1 AND 3650 AND access_for_default_days <= access_for_max_days));");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_version ADD CONSTRAINT ck_entitlement_version_acceptance CHECK ((owner_accepted_by IS NULL) = (owner_accepted_at_utc IS NULL));");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_entitlement_version_one_draft ON catalog.entitlement_version (entitlement_id) WHERE state = 'Draft';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_entitlement_version_one_active ON catalog.entitlement_version (entitlement_id) WHERE state = 'Active';");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_change_request ADD CONSTRAINT ck_entitlement_change_request_state CHECK (
    (state = 'Proposed' AND checker IS NULL AND decision_reason IS NULL AND decided_at_utc IS NULL)
    OR (state = 'Withdrawn' AND checker IS NOT NULL AND checker = maker AND decided_at_utc IS NOT NULL)
    OR (state IN ('Rejected', 'Approved') AND checker IS NOT NULL AND checker <> maker AND decided_at_utc IS NOT NULL
        AND (accepted_by IS NULL OR checker <> accepted_by)
        AND (owner_kind <> 'Person' OR checker <> owner_id)
        AND (state = 'Approved' OR decision_reason IS NOT NULL)));");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_change_request ADD CONSTRAINT ck_entitlement_change_request_content_hash_hex CHECK (content_hash ~ '^[0-9a-f]{64}$');");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_change_request ADD CONSTRAINT ck_entitlement_change_request_version_number CHECK (version_number >= 1);");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_change_request ADD CONSTRAINT ck_entitlement_change_request_owner_kind CHECK (owner_kind IN ('Person', 'Group'));");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_entitlement_change_request_one_pending ON catalog.entitlement_change_request (entitlement_id) WHERE state = 'Proposed';");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_change_request ADD CONSTRAINT ak_entitlement_change_request_id_version_id UNIQUE (id, version_id);");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_version ADD CONSTRAINT fk_entitlement_version_request_names FOREIGN KEY (activated_by_request_id, id) REFERENCES catalog.entitlement_change_request (id, version_id);");
            migrationBuilder.Sql(@"CREATE FUNCTION catalog.reject_entitlement_key_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'catalog.entitlement rows cannot be deleted (DL-087)';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.application_id IS DISTINCT FROM OLD.application_id OR NEW.key IS DISTINCT FROM OLD.key THEN
        RAISE EXCEPTION 'Entitlement id, application and key are immutable (DL-087)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION catalog.reject_non_draft_entitlement_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP = 'TRUNCATE' THEN
        RAISE EXCEPTION 'catalog.entitlement_version rows cannot be truncated (DL-087)';
    END IF;
    IF TG_OP = 'UPDATE' AND (NEW.id IS DISTINCT FROM OLD.id OR NEW.entitlement_id IS DISTINCT FROM OLD.entitlement_id
        OR NEW.version_number IS DISTINCT FROM OLD.version_number) THEN
        RAISE EXCEPTION 'Entitlement version id, entitlement and number are immutable (DL-087)';
    END IF;
    IF TG_OP = 'DELETE' THEN
        IF OLD.state <> 'Draft' THEN
            RAISE EXCEPTION 'Active or superseded entitlement versions are immutable (DL-087)';
        END IF;
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.state <> 'Draft' THEN
        IF NOT (OLD.state = 'Active' AND NEW.state = 'Superseded'
            AND (NEW.content_hash, NEW.owner_kind, NEW.owner_id, NEW.privileged, NEW.allowed_account_classes, NEW.technical_name, NEW.edited_by, NEW.owner_accepted_by, NEW.activated_by_request_id)
                IS NOT DISTINCT FROM (OLD.content_hash, OLD.owner_kind, OLD.owner_id, OLD.privileged, OLD.allowed_account_classes, OLD.technical_name, OLD.edited_by, OLD.owner_accepted_by, OLD.activated_by_request_id)) THEN
            RAISE EXCEPTION 'Active or superseded entitlement versions are immutable; an active version can only be superseded (DL-087)';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.state = 'Draft' THEN
        RETURN NEW;
    END IF;
    IF NEW.state <> 'Active' OR NOT EXISTS (
        SELECT 1 FROM catalog.entitlement_change_request r
        WHERE r.id = NEW.activated_by_request_id AND r.version_id = NEW.id AND r.state = 'Approved'
          AND r.entitlement_id = NEW.entitlement_id AND r.version_number = NEW.version_number AND r.content_hash = NEW.content_hash
          AND r.owner_kind = NEW.owner_kind AND r.owner_id = NEW.owner_id
          AND (r.accepted_by IS NULL OR r.accepted_by = NEW.owner_accepted_by)
          AND r.checker <> NEW.edited_by AND (NEW.owner_accepted_by IS NULL OR r.checker <> NEW.owner_accepted_by)
        FOR SHARE) THEN
        RAISE EXCEPTION 'An entitlement version becomes active only by an approved request naming it, decided by someone who neither wrote it nor accepted it (DL-087)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION catalog.reject_decided_entitlement_request_change() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'catalog.entitlement_change_request rows are immutable and cannot be deleted (DL-087)';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.entitlement_id IS DISTINCT FROM OLD.entitlement_id
        OR NEW.version_id IS DISTINCT FROM OLD.version_id OR NEW.version_number IS DISTINCT FROM OLD.version_number
        OR NEW.content_hash IS DISTINCT FROM OLD.content_hash OR NEW.owner_kind IS DISTINCT FROM OLD.owner_kind
        OR NEW.owner_id IS DISTINCT FROM OLD.owner_id OR NEW.accepted_by IS DISTINCT FROM OLD.accepted_by
        OR NEW.maker IS DISTINCT FROM OLD.maker OR NEW.reason IS DISTINCT FROM OLD.reason
        OR NEW.proposed_at_utc IS DISTINCT FROM OLD.proposed_at_utc THEN
        RAISE EXCEPTION 'catalog.entitlement_change_request proposal fields are immutable (DL-087)';
    END IF;
    IF OLD.state <> 'Proposed' THEN
        RAISE EXCEPTION 'Decided catalog.entitlement_change_request rows are immutable (DL-087)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION catalog.reject_entitlement_key_change(), catalog.reject_non_draft_entitlement_change(), catalog.reject_decided_entitlement_request_change() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_entitlement_key_immutable BEFORE UPDATE OR DELETE ON catalog.entitlement FOR EACH ROW EXECUTE FUNCTION catalog.reject_entitlement_key_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_entitlement_no_truncate BEFORE TRUNCATE ON catalog.entitlement FOR EACH STATEMENT EXECUTE FUNCTION catalog.reject_entitlement_key_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_entitlement_version_immutable BEFORE INSERT OR UPDATE OR DELETE ON catalog.entitlement_version FOR EACH ROW EXECUTE FUNCTION catalog.reject_non_draft_entitlement_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_entitlement_version_no_truncate BEFORE TRUNCATE ON catalog.entitlement_version FOR EACH STATEMENT EXECUTE FUNCTION catalog.reject_non_draft_entitlement_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_entitlement_change_request_decided_immutable BEFORE UPDATE OR DELETE ON catalog.entitlement_change_request FOR EACH ROW EXECUTE FUNCTION catalog.reject_decided_entitlement_request_change();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_entitlement_change_request_no_truncate BEFORE TRUNCATE ON catalog.entitlement_change_request FOR EACH STATEMENT EXECUTE FUNCTION catalog.reject_decided_entitlement_request_change();");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement ENABLE ALWAYS TRIGGER tr_entitlement_key_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement ENABLE ALWAYS TRIGGER tr_entitlement_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_version ENABLE ALWAYS TRIGGER tr_entitlement_version_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_version ENABLE ALWAYS TRIGGER tr_entitlement_version_no_truncate;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_change_request ENABLE ALWAYS TRIGGER tr_entitlement_change_request_decided_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_change_request ENABLE ALWAYS TRIGGER tr_entitlement_change_request_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the entitlement catalog with its history; only valid on a database that holds no entitlement data worth keeping (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_entitlement_change_request_no_truncate ON catalog.entitlement_change_request;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_entitlement_change_request_decided_immutable ON catalog.entitlement_change_request;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_entitlement_version_no_truncate ON catalog.entitlement_version;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_entitlement_version_immutable ON catalog.entitlement_version;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_entitlement_no_truncate ON catalog.entitlement;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_entitlement_key_immutable ON catalog.entitlement;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS catalog.reject_entitlement_key_change(), catalog.reject_non_draft_entitlement_change(), catalog.reject_decided_entitlement_request_change();");
            migrationBuilder.Sql(@"ALTER TABLE catalog.entitlement_version DROP CONSTRAINT fk_entitlement_version_request_names;");
            migrationBuilder.DropForeignKey(
                name: "fk_entitlement_change_request_entitlement_entitlement_id",
                schema: "catalog",
                table: "entitlement_change_request");

            migrationBuilder.DropForeignKey(
                name: "fk_entitlement_version_entitlement_entitlement_id",
                schema: "catalog",
                table: "entitlement_version");

            migrationBuilder.DropForeignKey(
                name: "fk_entitlement_change_request_entitlement_version_version_id",
                schema: "catalog",
                table: "entitlement_change_request");

            migrationBuilder.DropTable(
                name: "entitlement",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "entitlement_version",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "entitlement_change_request",
                schema: "catalog");
        }
    }
}
