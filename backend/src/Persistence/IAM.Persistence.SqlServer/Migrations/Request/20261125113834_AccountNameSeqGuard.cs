using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Request
{
    /// <inheritdoc />
    public partial class AccountNameSeqGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-102: two names of one item written at the same moment leave "latest" open: the upgrade stops for a person.
            migrationBuilder.Sql(@"IF EXISTS (SELECT 1 FROM [request].[AccountNameAssignment] a
           JOIN [request].[AccountNameAssignment] b ON a.[ItemId] = b.[ItemId] AND a.[Id] <> b.[Id] AND a.[SetAtUtc] = b.[SetAtUtc])
    THROW 51541, N'an item has two account names written at the same moment, so which is latest is unknown; resolve them before upgrading (DL-102)', 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the guard changed nothing.
        }
    }
}
