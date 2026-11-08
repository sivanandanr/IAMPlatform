using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Identity
{
    /// <inheritdoc />
    public partial class AttributeCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "AttributeDefinition",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttributeDefinition", x => x.Id);
                    table.CheckConstraint("CK_AttributeDefinition_Key_Code", "DATALENGTH([Key]) > 0 AND DATALENGTH([Key]) = DATALENGTH(LTRIM(RTRIM([Key])))");
                });

            migrationBuilder.CreateTable(
                name: "AttributeDefinitionVersion",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttributeDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SubjectScope = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SourceSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Sensitivity = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Normalization = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    FreshnessMaxAgeMinutes = table.Column<int>(type: "int", nullable: false),
                    MaxLength = table.Column<int>(type: "int", nullable: true),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttributeDefinitionVersion", x => x.Id);
                    table.CheckConstraint("CK_AttributeDefinitionVersion_ContentHash_Code", "DATALENGTH([ContentHash]) > 0 AND DATALENGTH([ContentHash]) = DATALENGTH(LTRIM(RTRIM([ContentHash])))");
                    table.CheckConstraint("CK_AttributeDefinitionVersion_DataType_Code", "DATALENGTH([DataType]) > 0 AND DATALENGTH([DataType]) = DATALENGTH(LTRIM(RTRIM([DataType])))");
                    table.CheckConstraint("CK_AttributeDefinitionVersion_Normalization_Code", "DATALENGTH([Normalization]) > 0 AND DATALENGTH([Normalization]) = DATALENGTH(LTRIM(RTRIM([Normalization])))");
                    table.CheckConstraint("CK_AttributeDefinitionVersion_Sensitivity_Code", "DATALENGTH([Sensitivity]) > 0 AND DATALENGTH([Sensitivity]) = DATALENGTH(LTRIM(RTRIM([Sensitivity])))");
                    table.CheckConstraint("CK_AttributeDefinitionVersion_SourceSystem_Code", "DATALENGTH([SourceSystem]) > 0 AND DATALENGTH([SourceSystem]) = DATALENGTH(LTRIM(RTRIM([SourceSystem])))");
                    table.CheckConstraint("CK_AttributeDefinitionVersion_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.CheckConstraint("CK_AttributeDefinitionVersion_SubjectScope_Code", "DATALENGTH([SubjectScope]) > 0 AND DATALENGTH([SubjectScope]) = DATALENGTH(LTRIM(RTRIM([SubjectScope])))");
                    table.ForeignKey(
                        name: "FK_AttributeDefinitionVersion_AttributeDefinition_AttributeDefinitionId",
                        column: x => x.AttributeDefinitionId,
                        principalSchema: "identity",
                        principalTable: "AttributeDefinition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttributeDefinitionVersionOperator",
                schema: "identity",
                columns: table => new
                {
                    AttributeDefinitionVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operator = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttributeDefinitionVersionOperator", x => new { x.AttributeDefinitionVersionId, x.Operator });
                    table.CheckConstraint("CK_AttributeDefinitionVersionOperator_Operator_Code", "DATALENGTH([Operator]) > 0 AND DATALENGTH([Operator]) = DATALENGTH(LTRIM(RTRIM([Operator])))");
                    table.ForeignKey(
                        name: "FK_AttributeDefinitionVersionOperator_AttributeDefinitionVersion_AttributeDefinitionVersionId",
                        column: x => x.AttributeDefinitionVersionId,
                        principalSchema: "identity",
                        principalTable: "AttributeDefinitionVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttributeDefinition_Key",
                schema: "identity",
                table: "AttributeDefinition",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttributeDefinitionVersion_AttributeDefinitionId_VersionNumber",
                schema: "identity",
                table: "AttributeDefinitionVersion",
                columns: new[] { "AttributeDefinitionId", "VersionNumber" },
                unique: true);

            // ADR-0015 / IAM-WF-019 immutability and the DL-007 publish lock, hand-written and reviewed (not generated).
            // Trigger names match the HasTrigger declarations in IdentityDbContext. Trigger errors carry "immutable".
            //
            // DL-007: until maker-checker exists, the database itself allows only Draft versions, so no login (including
            // the Identity app login with schema-wide DML) can publish by SQL. A reviewed migration lifts this constraint
            // together with the maker-checker workflow.
            migrationBuilder.Sql("ALTER TABLE [identity].[AttributeDefinitionVersion] ADD CONSTRAINT [CK_AttributeDefinitionVersion_State_DraftOnly] CHECK ([State] = N'Draft');");

            // At most one draft and one published version per attribute; the reader and the domain rely on it.
            migrationBuilder.Sql("CREATE UNIQUE INDEX [UX_AttributeDefinitionVersion_OneDraft] ON [identity].[AttributeDefinitionVersion] ([AttributeDefinitionId]) WHERE [State] = N'Draft';");
            migrationBuilder.Sql("CREATE UNIQUE INDEX [UX_AttributeDefinitionVersion_OnePublished] ON [identity].[AttributeDefinitionVersion] ([AttributeDefinitionId]) WHERE [State] = N'Published';");

            // The key never changes. Refuses any UPDATE that names the column, independent of the primary key.
            migrationBuilder.Sql(@"CREATE TRIGGER [identity].[TR_AttributeDefinition_KeyImmutable] ON [identity].[AttributeDefinition]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE([Key]) OR UPDATE([Id])
        THROW 51010, 'identity.AttributeDefinition.Key and Id are immutable (ADR-0015).', 1;
END;");

            // A version's identity never changes; a non-draft version never changes or disappears.
            migrationBuilder.Sql(@"CREATE TRIGGER [identity].[TR_AttributeDefinitionVersion_Immutable] ON [identity].[AttributeDefinitionVersion]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND (UPDATE([Id]) OR UPDATE([AttributeDefinitionId]) OR UPDATE([VersionNumber]))
        THROW 51013, 'Attribute definition version Id, AttributeDefinitionId and VersionNumber are immutable (IAM-WF-019).', 1;
    IF EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51011, 'Published or retired attribute definition versions are immutable (IAM-WF-019).', 1;
END;");

            // Operators of a non-draft version never change. READCOMMITTEDLOCK reads the committed parent state even
            // under read-committed snapshot, so a concurrent state change cannot slip past.
            migrationBuilder.Sql(@"CREATE TRIGGER [identity].[TR_AttributeDefinitionVersionOperator_Immutable] ON [identity].[AttributeDefinitionVersionOperator]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM (SELECT [AttributeDefinitionVersionId] FROM inserted UNION ALL SELECT [AttributeDefinitionVersionId] FROM deleted) x
        JOIN [identity].[AttributeDefinitionVersion] v WITH (READCOMMITTEDLOCK) ON v.[Id] = x.[AttributeDefinitionVersionId]
        WHERE v.[State] <> N'Draft')
        THROW 51012, 'Operators of published or retired attribute definition versions are immutable (IAM-WF-019).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttributeDefinitionVersionOperator",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "AttributeDefinitionVersion",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "AttributeDefinition",
                schema: "identity");
        }
    }
}
