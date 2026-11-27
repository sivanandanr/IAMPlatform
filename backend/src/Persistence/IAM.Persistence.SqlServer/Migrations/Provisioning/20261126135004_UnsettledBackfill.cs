using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class UnsettledBackfill : Migration
    {
        /// <summary>
        /// DL-103: a create or add that broke off before DL-102 waits unmarked. Every planned create or add of an item that has
        /// already been tried is marked unsettled, so its "already there" is never taken as someone else's. Conservative: some
        /// may go to a person needlessly; none is trusted wrongly. Public so a test runs exactly this statement.
        /// </summary>
        public const string Sql = @"UPDATE o SET o.[Unsettled] = 1
FROM [provisioning].[ProvisioningOperation] o
JOIN [provisioning].[ProvisioningItem] i ON i.[Id] = o.[ItemId]
WHERE o.[State] = N'Planned' AND o.[Action] IN (N'account.create', N'account.entitlement.add')
    AND i.[State] IN (N'Planned', N'Running') AND i.[Attempts] > 0 AND o.[Unsettled] = 0;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020): the mark can't be cleared (DL-102), and nothing else changed.
        }
    }
}
