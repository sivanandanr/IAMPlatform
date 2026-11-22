using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Policy
{
    /// <inheritdoc />
    public partial class MatrixEditorsRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-099, hand-written and reviewed (not generated). Every matrix version names its writers: versions from before DL-098
            // are given their one known writer (EditedBy), with the immutability trigger paused for this backfill only.
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_version DISABLE TRIGGER tr_matrix_version_immutable;");
            migrationBuilder.Sql(@"UPDATE policy.matrix_version SET editors = edited_by WHERE editors IS NULL;");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_version ENABLE ALWAYS TRIGGER tr_matrix_version_immutable;");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_version ALTER COLUMN editors SET NOT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_version ADD CONSTRAINT ck_matrix_version_editors CHECK (editors <> '');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020, DL-099): Down restores the earlier rules for an empty development database only; with data, recover by
            // restore and a compensating forward migration.
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_version DROP CONSTRAINT ck_matrix_version_editors;");
            migrationBuilder.Sql(@"ALTER TABLE policy.matrix_version ALTER COLUMN editors DROP NOT NULL;");
        }
    }
}
