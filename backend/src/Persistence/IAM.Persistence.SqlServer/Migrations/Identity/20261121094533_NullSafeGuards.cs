using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Identity
{
    /// <inheritdoc />
    public partial class NullSafeGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-095, hand-written and reviewed (not generated). The DL-064 guard compared MaxLength as
            // `i = d OR (both NULL)`, which is UNKNOWN when exactly one side is NULL. NOT UNKNOWN is UNKNOWN, so retiring a
            // published version while setting or clearing MaxLength slipped through. DisplayName was compared under a binary
            // collation, which still ignores trailing spaces.
            // SQL Server only: PostgreSQL compares the same columns as a row with IS NOT DISTINCT FROM and needs no change.
            // Columns are compared as one row with INTERSECT, which treats two NULLs as equal and NULL against a value as
            // different. Strings are compared as bytes plus byte length, so case, trailing spaces and trailing NUL characters
            // all count, as they do on PostgreSQL.
            migrationBuilder.Sql(@"ALTER TRIGGER [identity].[TR_AttributeDefinitionVersion_Immutable] ON [identity].[AttributeDefinitionVersion]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([AttributeDefinitionId]) OR UPDATE([VersionNumber]))
        THROW 51013, 'Attribute definition version Id, AttributeDefinitionId and VersionNumber are immutable (IAM-WF-019).', 1;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51011, 'Published or retired attribute definition versions are immutable (IAM-WF-019).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE d.[State] <> N'Draft' AND NOT (
            d.[State] = N'Published' AND i.[State] = N'Retired'
            AND EXISTS (
                SELECT CAST(i.[DisplayName] AS varbinary(400)), DATALENGTH(i.[DisplayName]),
                       CAST(i.[DataType] AS varbinary(32)), DATALENGTH(i.[DataType]),
                       CAST(i.[SubjectScope] AS varbinary(32)), DATALENGTH(i.[SubjectScope]),
                       CAST(i.[SourceSystem] AS varbinary(128)), DATALENGTH(i.[SourceSystem]),
                       CAST(i.[Sensitivity] AS varbinary(32)), DATALENGTH(i.[Sensitivity]),
                       CAST(i.[Normalization] AS varbinary(64)), DATALENGTH(i.[Normalization]),
                       i.[FreshnessMaxAgeMinutes],
                       i.[MaxLength],
                       CAST(i.[ContentHash] AS varbinary(128)), DATALENGTH(i.[ContentHash]),
                       i.[PublishedByRequestId]
                INTERSECT
                SELECT CAST(d.[DisplayName] AS varbinary(400)), DATALENGTH(d.[DisplayName]),
                       CAST(d.[DataType] AS varbinary(32)), DATALENGTH(d.[DataType]),
                       CAST(d.[SubjectScope] AS varbinary(32)), DATALENGTH(d.[SubjectScope]),
                       CAST(d.[SourceSystem] AS varbinary(128)), DATALENGTH(d.[SourceSystem]),
                       CAST(d.[Sensitivity] AS varbinary(32)), DATALENGTH(d.[Sensitivity]),
                       CAST(d.[Normalization] AS varbinary(64)), DATALENGTH(d.[Normalization]),
                       d.[FreshnessMaxAgeMinutes],
                       d.[MaxLength],
                       CAST(d.[ContentHash] AS varbinary(128)), DATALENGTH(d.[ContentHash]),
                       d.[PublishedByRequestId])))
        THROW 51011, 'Published or retired attribute definition versions are immutable; a published version can only be retired (IAM-WF-019, DL-064).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        WHERE i.[State] <> N'Draft' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND (i.[State] <> N'Published' OR NOT EXISTS (
            SELECT 1 FROM [identity].[AttributePublishRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[PublishedByRequestId] AND r.[AttributeDefinitionVersionId] = i.[Id] AND r.[State] = N'Approved'
            AND r.[AttributeDefinitionId] = i.[AttributeDefinitionId] AND r.[VersionNumber] = i.[VersionNumber] AND r.[ContentHash] = i.[ContentHash])))
        THROW 51014, 'An attribute version can only be published by an approved publish request naming it (DL-007, DL-064).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores the merged trigger exactly, gap included; only for rolling back this migration (DL-020).
            migrationBuilder.Sql(@"ALTER TRIGGER [identity].[TR_AttributeDefinitionVersion_Immutable] ON [identity].[AttributeDefinitionVersion]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([AttributeDefinitionId]) OR UPDATE([VersionNumber]))
        THROW 51013, 'Attribute definition version Id, AttributeDefinitionId and VersionNumber are immutable (IAM-WF-019).', 1;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51011, 'Published or retired attribute definition versions are immutable (IAM-WF-019).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE d.[State] <> N'Draft' AND NOT (
            d.[State] = N'Published' AND i.[State] = N'Retired'
            AND i.[DisplayName] COLLATE Latin1_General_100_BIN2 = d.[DisplayName] COLLATE Latin1_General_100_BIN2
            AND i.[DataType] = d.[DataType] AND i.[SubjectScope] = d.[SubjectScope] AND i.[SourceSystem] = d.[SourceSystem]
            AND i.[Sensitivity] = d.[Sensitivity] AND i.[Normalization] = d.[Normalization]
            AND i.[FreshnessMaxAgeMinutes] = d.[FreshnessMaxAgeMinutes]
            AND (i.[MaxLength] = d.[MaxLength] OR (i.[MaxLength] IS NULL AND d.[MaxLength] IS NULL))
            AND i.[ContentHash] = d.[ContentHash] AND i.[PublishedByRequestId] = d.[PublishedByRequestId]))
        THROW 51011, 'Published or retired attribute definition versions are immutable; a published version can only be retired (IAM-WF-019, DL-064).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        WHERE i.[State] <> N'Draft' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND (i.[State] <> N'Published' OR NOT EXISTS (
            SELECT 1 FROM [identity].[AttributePublishRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[PublishedByRequestId] AND r.[AttributeDefinitionVersionId] = i.[Id] AND r.[State] = N'Approved'
            AND r.[AttributeDefinitionId] = i.[AttributeDefinitionId] AND r.[VersionNumber] = i.[VersionNumber] AND r.[ContentHash] = i.[ContentHash])))
        THROW 51014, 'An attribute version can only be published by an approved publish request naming it (DL-007, DL-064).', 1;
END;");
        }
    }
}
