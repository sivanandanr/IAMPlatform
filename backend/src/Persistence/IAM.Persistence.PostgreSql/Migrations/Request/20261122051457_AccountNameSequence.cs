using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Request
{
    /// <inheritdoc />
    public partial class AccountNameSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-100: the latest account name is the last one written (an identity in insertion order), never a host's clock.
            migrationBuilder.AddColumn<long>(
                name: "seq",
                schema: "request",
                table: "account_name_assignment",
                type: "bigint",
                nullable: false)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.CreateIndex(
                name: "ix_account_name_assignment_item_id_seq",
                schema: "request",
                table: "account_name_assignment",
                columns: new[] { "item_id", "seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020): Down is for an empty development database; with data, recover by restore and a compensating
            // forward migration.
            migrationBuilder.DropIndex(
                name: "ix_account_name_assignment_item_id_seq",
                schema: "request",
                table: "account_name_assignment");

            migrationBuilder.DropColumn(
                name: "seq",
                schema: "request",
                table: "account_name_assignment");
        }
    }
}
