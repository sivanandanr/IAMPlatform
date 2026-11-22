using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class EntitlementVersionGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-098, hand-written and reviewed (not generated). Retiring an active version may change its state only: every column
            // its content hash covers, and its owner's acceptance and creation, stay as they were (the stage 8 review found Type,
            // Description, EscalationWorkgroup, Risk and the AccessFor* columns could change). Same comparison rules as DL-095.
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION catalog.reject_non_draft_entitlement_change() RETURNS trigger
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
            AND (NEW.type, NEW.technical_name, NEW.description, NEW.owner_kind, NEW.owner_id, NEW.escalation_workgroup, NEW.privileged, NEW.risk, NEW.allowed_account_classes, NEW.access_for_mode, NEW.access_for_days, NEW.access_for_default_days, NEW.access_for_max_days, NEW.content_hash, NEW.edited_by, NEW.owner_accepted_by, NEW.owner_accepted_at_utc, NEW.activated_by_request_id, NEW.created_at_utc, NEW.created_by)
                IS NOT DISTINCT FROM (OLD.type, OLD.technical_name, OLD.description, OLD.owner_kind, OLD.owner_id, OLD.escalation_workgroup, OLD.privileged, OLD.risk, OLD.allowed_account_classes, OLD.access_for_mode, OLD.access_for_days, OLD.access_for_default_days, OLD.access_for_max_days, OLD.content_hash, OLD.edited_by, OLD.owner_accepted_by, OLD.owner_accepted_at_utc, OLD.activated_by_request_id, OLD.created_at_utc, OLD.created_by)) THEN
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"CREATE OR REPLACE FUNCTION catalog.reject_non_draft_entitlement_change() RETURNS trigger
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
        }
    }
}
