using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Policy
{
    /// <inheritdoc />
    public partial class MatrixEditorsRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-099, hand-written and reviewed (not generated). Every matrix version names its writers: versions from before DL-098
            // are given their one known writer (EditedBy), with the immutability trigger paused for this backfill only.
            migrationBuilder.Sql(@"DISABLE TRIGGER [policy].[TR_MatrixVersion_Immutable] ON [policy].[MatrixVersion];");
            migrationBuilder.Sql(@"UPDATE [policy].[MatrixVersion] SET [Editors] = [EditedBy] WHERE [Editors] IS NULL;");
            migrationBuilder.Sql(@"ENABLE TRIGGER [policy].[TR_MatrixVersion_Immutable] ON [policy].[MatrixVersion];");
            migrationBuilder.Sql(@"ALTER TABLE [policy].[MatrixVersion] ALTER COLUMN [Editors] nvarchar(max) NOT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE [policy].[MatrixVersion] ADD CONSTRAINT [CK_MatrixVersion_Editors] CHECK (DATALENGTH([Editors]) > 0);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020, DL-099): Down restores the earlier rules for an empty development database only; with data, recover by
            // restore and a compensating forward migration.
            migrationBuilder.Sql(@"ALTER TABLE [policy].[MatrixVersion] DROP CONSTRAINT [CK_MatrixVersion_Editors];");
            migrationBuilder.Sql(@"ALTER TABLE [policy].[MatrixVersion] ALTER COLUMN [Editors] nvarchar(max) NULL;");
        }
    }
}
