using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class ProvisioningUndoTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "postponements",
                schema: "provisioning",
                table: "provisioning_item",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "undo_attempts",
                schema: "provisioning",
                table: "provisioning_item",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "undo_reason",
                schema: "provisioning",
                table: "provisioning_item",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                collation: "C");

            migrationBuilder.CreateTable(
                name: "grant_lock",
                schema: "provisioning",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    held_by = table.Column<Guid>(type: "uuid", nullable: false),
                    until_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grant_lock", x => new { x.account_id, x.entitlement_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_item_account_id_entitlement_id",
                schema: "provisioning",
                table: "provisioning_item",
                columns: new[] { "account_id", "entitlement_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_provisioning_item_undo_reason_code",
                schema: "provisioning",
                table: "provisioning_item",
                sql: "\"undo_reason\" <> '' AND \"undo_reason\" = btrim(\"undo_reason\")");
            // DL-100, hand-written and reviewed (not generated): undo passes, postponements and the undo reason stay in range; a
            // grant lock names an account, an entitlement and its holder.
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item ADD CONSTRAINT ck_provisioning_item_undo CHECK (undo_attempts BETWEEN 0 AND 1000 AND postponements BETWEEN 0 AND 1000 AND (undo_reason IS NULL OR undo_attempts >= 1));");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.grant_lock ADD CONSTRAINT ck_grant_lock_ids CHECK (account_id <> '00000000-0000-0000-0000-000000000000' AND entitlement_id <> '00000000-0000-0000-0000-000000000000' AND held_by <> '00000000-0000-0000-0000-000000000000');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020): Down is for an empty development database; with data, recover by restore and a compensating
            // forward migration.
            migrationBuilder.Sql(@"ALTER TABLE provisioning.provisioning_item DROP CONSTRAINT ck_provisioning_item_undo;");
            migrationBuilder.DropTable(
                name: "grant_lock",
                schema: "provisioning");

            migrationBuilder.DropIndex(
                name: "ix_provisioning_item_account_id_entitlement_id",
                schema: "provisioning",
                table: "provisioning_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_provisioning_item_undo_reason_code",
                schema: "provisioning",
                table: "provisioning_item");

            migrationBuilder.DropColumn(
                name: "postponements",
                schema: "provisioning",
                table: "provisioning_item");

            migrationBuilder.DropColumn(
                name: "undo_attempts",
                schema: "provisioning",
                table: "provisioning_item");

            migrationBuilder.DropColumn(
                name: "undo_reason",
                schema: "provisioning",
                table: "provisioning_item");
        }
    }
}
