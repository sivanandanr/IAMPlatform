using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class GrantLockClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                schema: "provisioning",
                table: "GrantLock",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // DL-101: each lock names the run holding it. Locks held now get a token no run has, so they only run out.
            migrationBuilder.Sql("UPDATE [provisioning].[GrantLock] SET [ClaimToken] = NEWID();");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[GrantLock] ADD CONSTRAINT [CK_GrantLock_Claim] CHECK ([ClaimToken] <> '00000000-0000-0000-0000-000000000000');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020): Down is for an empty development database; with data, recover by restore and a compensating
            // forward migration.
            migrationBuilder.Sql("ALTER TABLE [provisioning].[GrantLock] DROP CONSTRAINT [CK_GrantLock_Claim];");
            migrationBuilder.DropColumn(
                name: "ClaimToken",
                schema: "provisioning",
                table: "GrantLock");
        }
    }
}
