using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Account
{
    /// <inheritdoc />
    public partial class AccountNameHyphen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-091 review of DL-089, hand-written and reviewed (not generated). SQL Server reads a hyphen after other characters in
            // LIKE brackets as a range, so the name check refused names with a hyphen that the domain and PostgreSQL allow. The
            // hyphen goes first, where it is literal. PostgreSQL's check (a regular expression) was right and has no migration.
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] DROP CONSTRAINT [CK_Account_Name];");
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] ADD CONSTRAINT [CK_Account_Name] CHECK (LEN([Name]) BETWEEN 1 AND 256 AND DATALENGTH([Name]) = 2 * LEN([Name]) AND [Name] NOT LIKE '[-._]%' AND [Name] NOT LIKE '%[^-A-Za-z0-9._]%' AND CAST([NameKey] AS VARBINARY(512)) = CAST(LOWER([Name]) AS VARBINARY(512)));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] DROP CONSTRAINT [CK_Account_Name];");
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] ADD CONSTRAINT [CK_Account_Name] CHECK (LEN([Name]) BETWEEN 1 AND 256 AND DATALENGTH([Name]) = 2 * LEN([Name]) AND [Name] NOT LIKE '[._-]%' AND [Name] NOT LIKE '%[^A-Za-z0-9._-]%' AND CAST([NameKey] AS VARBINARY(512)) = CAST(LOWER([Name]) AS VARBINARY(512)));");
        }
    }
}
