using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Policy
{
    /// <inheritdoc />
    public partial class MatrixGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Editors",
                schema: "policy",
                table: "MatrixVersion",
                type: "nvarchar(max)",
                maxLength: 4100,
                nullable: true);
            // DL-098, hand-written and reviewed (not generated). A retired matrix version is compared exactly, as on PostgreSQL (the
            // document by bytes and length, not the database's case-insensitive collation), its editors included; and no one who
            // wrote any of the draft may be the checker who activates it.
            migrationBuilder.Sql(@"ALTER TRIGGER [policy].[TR_MatrixVersion_Immutable] ON [policy].[MatrixVersion]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([MatrixId]) OR UPDATE([VersionNumber]))
        THROW 51303, 'Matrix version id, matrix and number are immutable (DL-090).', 1;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51304, 'Active or superseded matrix versions are immutable (DL-090).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE d.[State] <> N'Draft' AND NOT (
            d.[State] = N'Active' AND i.[State] = N'Superseded' AND EXISTS (
                SELECT CAST(i.[Document] AS varbinary(max)), DATALENGTH(i.[Document]),
                       CAST(i.[ContentHash] AS varbinary(128)), DATALENGTH(i.[ContentHash]),
                       CAST(i.[CompiledHash] AS varbinary(128)), DATALENGTH(i.[CompiledHash]),
                       CAST(i.[EditedBy] AS varbinary(400)), DATALENGTH(i.[EditedBy]),
                       CAST(i.[Editors] AS varbinary(max)), DATALENGTH(i.[Editors]),
                       i.[ActivatedByRequestId]
                INTERSECT
                SELECT CAST(d.[Document] AS varbinary(max)), DATALENGTH(d.[Document]),
                       CAST(d.[ContentHash] AS varbinary(128)), DATALENGTH(d.[ContentHash]),
                       CAST(d.[CompiledHash] AS varbinary(128)), DATALENGTH(d.[CompiledHash]),
                       CAST(d.[EditedBy] AS varbinary(400)), DATALENGTH(d.[EditedBy]),
                       CAST(d.[Editors] AS varbinary(max)), DATALENGTH(d.[Editors]),
                       d.[ActivatedByRequestId])))
        THROW 51304, 'Active or superseded matrix versions are immutable; an active version can only be superseded (DL-090).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        WHERE i.[State] <> N'Draft' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND (i.[State] <> N'Active' OR NOT EXISTS (
            SELECT 1 FROM [policy].[MatrixChangeRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[ActivatedByRequestId] AND r.[VersionId] = i.[Id] AND r.[State] = N'Approved'
            AND r.[MatrixId] = i.[MatrixId] AND r.[VersionNumber] = i.[VersionNumber] AND r.[ContentHash] = i.[ContentHash]
            AND r.[CompiledHash] = i.[CompiledHash] AND r.[Checker] <> i.[EditedBy]
            AND (i.[Editors] IS NULL OR CHARINDEX((NCHAR(10) + r.[Checker] + NCHAR(10)) COLLATE Latin1_General_100_BIN2, (NCHAR(10) + i.[Editors] + NCHAR(10)) COLLATE Latin1_General_100_BIN2) = 0))))
        THROW 51305, 'A matrix version becomes active only by an approved request naming it, decided by someone who did not write it (DL-090).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TRIGGER [policy].[TR_MatrixVersion_Immutable] ON [policy].[MatrixVersion]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([MatrixId]) OR UPDATE([VersionNumber]))
        THROW 51303, 'Matrix version id, matrix and number are immutable (DL-090).', 1;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51304, 'Active or superseded matrix versions are immutable (DL-090).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE d.[State] <> N'Draft' AND NOT (
            d.[State] = N'Active' AND i.[State] = N'Superseded' AND i.[Document] = d.[Document] AND i.[ContentHash] = d.[ContentHash]
            AND i.[CompiledHash] = d.[CompiledHash] AND i.[EditedBy] = d.[EditedBy] AND i.[ActivatedByRequestId] = d.[ActivatedByRequestId]))
        THROW 51304, 'Active or superseded matrix versions are immutable; an active version can only be superseded (DL-090).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        WHERE i.[State] <> N'Draft' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND (i.[State] <> N'Active' OR NOT EXISTS (
            SELECT 1 FROM [policy].[MatrixChangeRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[ActivatedByRequestId] AND r.[VersionId] = i.[Id] AND r.[State] = N'Approved'
            AND r.[MatrixId] = i.[MatrixId] AND r.[VersionNumber] = i.[VersionNumber] AND r.[ContentHash] = i.[ContentHash]
            AND r.[CompiledHash] = i.[CompiledHash] AND r.[Checker] <> i.[EditedBy])))
        THROW 51305, 'A matrix version becomes active only by an approved request naming it, decided by someone who did not write it (DL-090).', 1;
END;");
            migrationBuilder.DropColumn(
                name: "Editors",
                schema: "policy",
                table: "MatrixVersion");
        }
    }
}
