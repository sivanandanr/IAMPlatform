using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Request
{
    /// <inheritdoc />
    public partial class AccountNameSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-100: the latest account name is the last one written (an identity in insertion order), never a host's clock.
            migrationBuilder.AddColumn<long>(
                name: "Seq",
                schema: "request",
                table: "AccountNameAssignment",
                type: "bigint",
                nullable: false)
                .Annotation("SqlServer:Identity", "1, 1");

            migrationBuilder.CreateIndex(
                name: "IX_AccountNameAssignment_ItemId_Seq",
                schema: "request",
                table: "AccountNameAssignment",
                columns: new[] { "ItemId", "Seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020): Down is for an empty development database; with data, recover by restore and a compensating
            // forward migration.
            migrationBuilder.DropIndex(
                name: "IX_AccountNameAssignment_ItemId_Seq",
                schema: "request",
                table: "AccountNameAssignment");

            migrationBuilder.DropColumn(
                name: "Seq",
                schema: "request",
                table: "AccountNameAssignment");
        }
    }
}
