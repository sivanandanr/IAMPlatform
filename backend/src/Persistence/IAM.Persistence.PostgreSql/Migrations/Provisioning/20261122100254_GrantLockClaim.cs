using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class GrantLockClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "claim_token",
                schema: "provisioning",
                table: "grant_lock",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // DL-101: each lock names the run holding it. Locks held now get a token no run has, so they only run out.
            migrationBuilder.Sql("UPDATE provisioning.grant_lock SET claim_token = gen_random_uuid();");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.grant_lock ADD CONSTRAINT ck_grant_lock_claim CHECK (claim_token <> '00000000-0000-0000-0000-000000000000');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020): Down is for an empty development database; with data, recover by restore and a compensating
            // forward migration.
            migrationBuilder.Sql("ALTER TABLE provisioning.grant_lock DROP CONSTRAINT ck_grant_lock_claim;");
            migrationBuilder.DropColumn(
                name: "claim_token",
                schema: "provisioning",
                table: "grant_lock");
        }
    }
}
