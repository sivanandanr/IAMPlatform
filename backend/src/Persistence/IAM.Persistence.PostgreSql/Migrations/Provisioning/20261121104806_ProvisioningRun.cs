using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class ProvisioningRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at_utc",
                schema: "provisioning",
                table: "provisioning_operation",
                type: "timestamp(6) with time zone",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "result_code",
                schema: "provisioning",
                table: "provisioning_operation",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                schema: "provisioning",
                table: "provisioning_item",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "provisioning",
                table: "provisioning_item",
                type: "character(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "finished_at_utc",
                schema: "provisioning",
                table: "provisioning_item",
                type: "timestamp(6) with time zone",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_until_utc",
                schema: "provisioning",
                table: "provisioning_item",
                type: "timestamp(6) with time zone",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at_utc",
                schema: "provisioning",
                table: "provisioning_item",
                type: "timestamp(6) with time zone",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "recorded_account_id",
                schema: "provisioning",
                table: "provisioning_item",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_provisioning_operation_result_code_code",
                schema: "provisioning",
                table: "provisioning_operation",
                sql: "\"result_code\" <> '' AND \"result_code\" = btrim(\"result_code\")");

            migrationBuilder.AddCheckConstraint(
                name: "ck_provisioning_item_correlation_id_code",
                schema: "provisioning",
                table: "provisioning_item",
                sql: "\"correlation_id\" <> '' AND \"correlation_id\" = btrim(\"correlation_id\")");
            // DL-094, hand-written and reviewed (not generated); the DL-093 guards stay as they are. The correlation id is set once
            // and never changes; attempts never go down; the recorded account, a step's result and the finishing times are set once;
            // a succeeded item has finished, and a new account's has its recorded account. SQL Server compares null-safely.
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item ADD CONSTRAINT ck_provisioning_item_run CHECK (attempts BETWEEN 0 AND 1000 AND (correlation_id IS NULL OR correlation_id ~ '^[0-9a-f]{32}$') AND (state <> 'Succeeded' OR (finished_at_utc IS NOT NULL AND (new_account_type IS NULL OR recorded_account_id IS NOT NULL))) AND (recorded_account_id IS NULL OR (state = 'Succeeded' AND new_account_type IS NOT NULL)) AND (lease_until_utc IS NULL OR state = 'Running'));");
            migrationBuilder.Sql(@"CREATE FUNCTION provisioning.keep_item_run() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF NEW.correlation_id IS DISTINCT FROM OLD.correlation_id OR NEW.attempts < OLD.attempts
        OR (OLD.recorded_account_id IS NOT NULL AND NEW.recorded_account_id IS DISTINCT FROM OLD.recorded_account_id)
        OR (OLD.finished_at_utc IS NOT NULL AND NEW.finished_at_utc IS DISTINCT FROM OLD.finished_at_utc) THEN
        RAISE EXCEPTION 'A provisioning item''s correlation id never changes, its attempts never go down, and what it finished with is kept (DL-094)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"CREATE FUNCTION provisioning.keep_operation_result() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF (OLD.result_code IS NOT NULL AND NEW.result_code IS DISTINCT FROM OLD.result_code)
        OR (OLD.completed_at_utc IS NOT NULL AND NEW.completed_at_utc IS DISTINCT FROM OLD.completed_at_utc) THEN
        RAISE EXCEPTION 'A step''s result is kept once it finished (DL-094)';
    END IF;
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION provisioning.keep_item_run(), provisioning.keep_operation_result() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_provisioning_item_run BEFORE UPDATE ON provisioning.provisioning_item FOR EACH ROW EXECUTE FUNCTION provisioning.keep_item_run();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_provisioning_operation_run BEFORE UPDATE ON provisioning.provisioning_operation FOR EACH ROW EXECUTE FUNCTION provisioning.keep_operation_result();");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item ENABLE ALWAYS TRIGGER tr_provisioning_item_run;");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_operation ENABLE ALWAYS TRIGGER tr_provisioning_operation_run;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_provisioning_operation_run ON provisioning.provisioning_operation;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_provisioning_item_run ON provisioning.provisioning_item;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS provisioning.keep_operation_result();");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS provisioning.keep_item_run();");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item DROP CONSTRAINT ck_provisioning_item_run;");
            migrationBuilder.DropCheckConstraint(
                name: "ck_provisioning_operation_result_code_code",
                schema: "provisioning",
                table: "provisioning_operation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_provisioning_item_correlation_id_code",
                schema: "provisioning",
                table: "provisioning_item");

            migrationBuilder.DropColumn(
                name: "completed_at_utc",
                schema: "provisioning",
                table: "provisioning_operation");

            migrationBuilder.DropColumn(
                name: "result_code",
                schema: "provisioning",
                table: "provisioning_operation");

            migrationBuilder.DropColumn(
                name: "attempts",
                schema: "provisioning",
                table: "provisioning_item");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "provisioning",
                table: "provisioning_item");

            migrationBuilder.DropColumn(
                name: "finished_at_utc",
                schema: "provisioning",
                table: "provisioning_item");

            migrationBuilder.DropColumn(
                name: "lease_until_utc",
                schema: "provisioning",
                table: "provisioning_item");

            migrationBuilder.DropColumn(
                name: "next_attempt_at_utc",
                schema: "provisioning",
                table: "provisioning_item");

            migrationBuilder.DropColumn(
                name: "recorded_account_id",
                schema: "provisioning",
                table: "provisioning_item");
        }
    }
}
