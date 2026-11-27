using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class LedgerNoFailed : Migration
    {
        /// <summary>
        /// DL-106: the SDK never records a failed operation (DL-105), and the first record for an operation id wins, so a stored
        /// failure would poison it. The upgrade stops if one exists (a person finds who wrote it; nothing is deleted). Public so a
        /// test runs exactly this statement.
        /// </summary>
        public const string Check = @"IF EXISTS (SELECT 1 FROM [provisioning].[OperationLedger] WHERE [Status] = N'Failed')
    THROW 51002, N'the operation ledger holds a failed operation, which the connector SDK never records; find who wrote it and resolve it before upgrading (DL-106)', 1;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Check);
            migrationBuilder.Sql("ALTER TABLE [provisioning].[OperationLedger] DROP CONSTRAINT [CK_OperationLedger_Values];");
            migrationBuilder.Sql(@"ALTER TABLE [provisioning].[OperationLedger] ADD CONSTRAINT [CK_OperationLedger_Values] CHECK (LEN([Fingerprint]) = 64 AND [Fingerprint] NOT LIKE '%[^0-9a-f]%' AND [Status] IN (N'Applied', N'AlreadyInState', N'Rejected', N'Unsupported'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020): a failed record is never allowed back.
        }
    }
}
