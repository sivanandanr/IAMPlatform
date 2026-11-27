using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class LedgerNoFailed : Migration
    {
        /// <summary>
        /// DL-106: the SDK never records a failed operation (DL-105), and the first record for an operation id wins, so a stored
        /// failure would poison it. The upgrade stops if one exists (a person finds who wrote it; nothing is deleted). Public so a
        /// test runs exactly this statement.
        /// </summary>
        public const string Check = @"DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM provisioning.operation_ledger WHERE status = 'Failed') THEN
        RAISE EXCEPTION 'the operation ledger holds a failed operation, which the connector SDK never records; find who wrote it and resolve it before upgrading (DL-106)';
    END IF;
END $$;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Check);
            migrationBuilder.Sql("ALTER TABLE provisioning.operation_ledger DROP CONSTRAINT ck_operation_ledger_values;");
            migrationBuilder.Sql(@"ALTER TABLE provisioning.operation_ledger ADD CONSTRAINT ck_operation_ledger_values CHECK (fingerprint ~ '^[0-9a-f]{64}$' AND status IN ('Applied', 'AlreadyInState', 'Rejected', 'Unsupported'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020): a failed record is never allowed back.
        }
    }
}
