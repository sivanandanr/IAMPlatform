using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class GroupHoldersAreWorkgroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-068b, hand-written: a Group holder names an IAM workgroup by its key (Organization owns workgroups, so
            // existence is checked by the service through its contract; the key's shape is checked here).
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsRole] ADD CONSTRAINT [CK_ApplicationSettingsRole_GroupIsWorkgroupKey] CHECK ([HolderKind] IS NULL OR [HolderKind] <> N'Group' OR (LEN([HolderId]) BETWEEN 2 AND 64 AND DATALENGTH([HolderId]) = 2 * LEN([HolderId]) AND [HolderId] LIKE '[a-z]%' AND [HolderId] NOT LIKE '%[^a-z0-9_]%'));");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsRole] DROP CONSTRAINT [CK_ApplicationSettingsRole_GroupIsWorkgroupKey];");

        }
    }
}
