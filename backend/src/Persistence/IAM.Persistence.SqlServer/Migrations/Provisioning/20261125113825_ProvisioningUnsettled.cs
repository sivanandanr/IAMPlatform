using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Provisioning
{
    /// <inheritdoc />
    public partial class ProvisioningUnsettled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Unsettled",
                schema: "provisioning",
                table: "ProvisioningOperation",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // DL-102: a step whose earlier attempt may have changed the target stays marked, so no login can make an unknown
            // outcome look settled.
            migrationBuilder.Sql(@"CREATE TRIGGER [provisioning].[TR_ProvisioningOperation_Unsettled] ON [provisioning].[ProvisioningOperation]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id] WHERE d.[Unsettled] = 1 AND i.[Unsettled] = 0)
        THROW 51540, 'A step''s unknown outcome is kept (DL-102).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020): Down is for an empty development database; with data, recover by restore and a compensating
            // forward migration.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [provisioning].[TR_ProvisioningOperation_Unsettled];");
            migrationBuilder.DropColumn(
                name: "Unsettled",
                schema: "provisioning",
                table: "ProvisioningOperation");
        }
    }
}
