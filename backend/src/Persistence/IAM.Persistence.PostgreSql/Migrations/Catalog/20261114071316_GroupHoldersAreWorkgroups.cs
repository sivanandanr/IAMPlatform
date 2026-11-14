using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class GroupHoldersAreWorkgroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-068b, hand-written: a Group holder names an IAM workgroup by its key (Organization owns workgroups, so
            // existence is checked by the service through its contract; the key's shape is checked here).
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_role ADD CONSTRAINT ck_application_settings_role_group_is_workgroup_key CHECK (holder_kind IS NULL OR holder_kind <> 'Group' OR holder_id ~ '^[a-z][a-z0-9_]{1,63}$');");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE catalog.application_settings_role DROP CONSTRAINT ck_application_settings_role_group_is_workgroup_key;");

        }
    }
}
