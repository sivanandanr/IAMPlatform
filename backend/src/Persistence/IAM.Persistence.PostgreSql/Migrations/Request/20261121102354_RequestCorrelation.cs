using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Request
{
    /// <inheritdoc />
    public partial class RequestCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "request",
                table: "request_item",
                type: "character(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true,
                collation: "C");

            migrationBuilder.AddCheckConstraint(
                name: "ck_request_item_correlation_id_code",
                schema: "request",
                table: "request_item",
                sql: "\"correlation_id\" <> '' AND \"correlation_id\" = btrim(\"correlation_id\")");
            // DL-094, hand-written and reviewed (not generated). The correlation id is a W3C trace id: 32 lower-case hex digits.
            migrationBuilder.Sql(@"ALTER TABLE request.request_item ADD CONSTRAINT ck_request_item_correlation_id CHECK (correlation_id IS NULL OR correlation_id ~ '^[0-9a-f]{32}$');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE request.request_item DROP CONSTRAINT ck_request_item_correlation_id;");
            migrationBuilder.DropCheckConstraint(
                name: "ck_request_item_correlation_id_code",
                schema: "request",
                table: "request_item");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "request",
                table: "request_item");
        }
    }
}
