using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Account
{
    /// <inheritdoc />
    public partial class AccountTargetCanonical : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-099, hand-written and reviewed (not generated). The target-id key is now computed in C# only (a DN written one way,
            // AccountRules.TargetIdKey), so the database no longer compares it with LOWER(target_id); it checks that both are printable
            // ASCII (so C# and both databases lower them alike) and that the key is in lower case. Keys stored before DL-099 were
            // LOWER(target_id), which is the canonical form for a DN without spaces around separators or escapes; any other needs
            // its key recomputed by hand before this runs (R-040).
            migrationBuilder.Sql(@"ALTER TABLE account.account DROP CONSTRAINT ck_account_target_id_key;");
            migrationBuilder.Sql(@"ALTER TABLE account.account ADD CONSTRAINT ck_account_target_id_key CHECK ((target_id IS NULL AND target_id_key IS NULL) OR (target_id IS NOT NULL AND target_id_key IS NOT NULL AND target_id ~ '^[ -~]+$' AND target_id_key ~ '^[ -~]+$' AND target_id_key = lower(target_id_key)));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020, DL-099): Down restores the earlier rules for an empty development database only; with data, recover by
            // restore and a compensating forward migration.
            migrationBuilder.Sql(@"ALTER TABLE account.account DROP CONSTRAINT ck_account_target_id_key;");
            migrationBuilder.Sql(@"ALTER TABLE account.account ADD CONSTRAINT ck_account_target_id_key CHECK ((target_id IS NULL AND target_id_key IS NULL) OR (target_id IS NOT NULL AND target_id_key IS NOT NULL AND target_id_key = lower(target_id)));");
        }
    }
}
