using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class UnsettledBackfill : Migration
    {
        /// <summary>
        /// DL-103: a create or add that broke off before DL-102 waits unmarked. Every planned create or add of an item that has
        /// already been tried is marked unsettled, so its "already there" is never taken as someone else's. Conservative: some
        /// may go to a person needlessly; none is trusted wrongly. Public so a test runs exactly this statement.
        /// </summary>
        public const string Sql = @"UPDATE provisioning.provisioning_operation o SET unsettled = true
FROM provisioning.provisioning_item i
WHERE o.item_id = i.id AND o.state = 'Planned' AND o.action IN ('account.create', 'account.entitlement.add')
    AND i.state IN ('Planned', 'Running') AND i.attempts > 0 AND NOT o.unsettled;";

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
