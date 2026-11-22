using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Account
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
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] DROP CONSTRAINT [CK_Account_TargetIdKey];");
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] ADD CONSTRAINT [CK_Account_TargetIdKey] CHECK (([TargetId] IS NULL AND [TargetIdKey] IS NULL) OR ([TargetId] IS NOT NULL AND [TargetIdKey] IS NOT NULL AND [TargetId] COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^ -~]%' AND [TargetIdKey] COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^ -~]%' AND CAST([TargetIdKey] AS VARBINARY(1024)) = CAST(LOWER([TargetIdKey]) AS VARBINARY(1024))));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020, DL-099): Down restores the earlier rules for an empty development database only; with data, recover by
            // restore and a compensating forward migration.
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] DROP CONSTRAINT [CK_Account_TargetIdKey];");
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] ADD CONSTRAINT [CK_Account_TargetIdKey] CHECK (([TargetId] IS NULL AND [TargetIdKey] IS NULL) OR ([TargetId] IS NOT NULL AND [TargetIdKey] IS NOT NULL AND CAST([TargetIdKey] AS VARBINARY(1024)) = CAST(LOWER([TargetId]) AS VARBINARY(1024))));");
        }
    }
}
