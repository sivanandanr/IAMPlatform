using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class NullSafeGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-095, hand-written and reviewed (not generated). The DL-087 guard compared OwnerAcceptedBy as
            // `(both NULL) OR i = d`, which is UNKNOWN when exactly one side is NULL. NOT UNKNOWN is UNKNOWN, so superseding an
            // active version while setting or clearing OwnerAcceptedBy slipped through. TechnicalName used the database's
            // case-insensitive collation, so a change of case or a trailing space slipped through too.
            // SQL Server only: PostgreSQL compares the same columns as a row with IS NOT DISTINCT FROM and needs no change.
            // Columns are compared as one row with INTERSECT, which treats two NULLs as equal and NULL against a value as
            // different. Strings are compared as bytes plus byte length, so case, trailing spaces and trailing NUL characters
            // all count, as they do on PostgreSQL.
            migrationBuilder.Sql(@"ALTER TRIGGER [catalog].[TR_EntitlementVersion_Immutable] ON [catalog].[EntitlementVersion]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([EntitlementId]) OR UPDATE([VersionNumber]))
        THROW 51122, 'Entitlement version Id, EntitlementId and VersionNumber are immutable (DL-087).', 1;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51123, 'Active or superseded entitlement versions are immutable (DL-087).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE d.[State] <> N'Draft' AND NOT (
            d.[State] = N'Active' AND i.[State] = N'Superseded'
            AND EXISTS (
                SELECT CAST(i.[ContentHash] AS varbinary(128)), DATALENGTH(i.[ContentHash]),
                       CAST(i.[OwnerKind] AS varbinary(32)), DATALENGTH(i.[OwnerKind]),
                       CAST(i.[OwnerId] AS varbinary(400)), DATALENGTH(i.[OwnerId]),
                       i.[Privileged],
                       CAST(i.[AllowedAccountClasses] AS varbinary(128)), DATALENGTH(i.[AllowedAccountClasses]),
                       CAST(i.[TechnicalName] AS varbinary(512)), DATALENGTH(i.[TechnicalName]),
                       CAST(i.[EditedBy] AS varbinary(400)), DATALENGTH(i.[EditedBy]),
                       i.[ActivatedByRequestId],
                       CAST(i.[OwnerAcceptedBy] AS varbinary(400)), DATALENGTH(i.[OwnerAcceptedBy])
                INTERSECT
                SELECT CAST(d.[ContentHash] AS varbinary(128)), DATALENGTH(d.[ContentHash]),
                       CAST(d.[OwnerKind] AS varbinary(32)), DATALENGTH(d.[OwnerKind]),
                       CAST(d.[OwnerId] AS varbinary(400)), DATALENGTH(d.[OwnerId]),
                       d.[Privileged],
                       CAST(d.[AllowedAccountClasses] AS varbinary(128)), DATALENGTH(d.[AllowedAccountClasses]),
                       CAST(d.[TechnicalName] AS varbinary(512)), DATALENGTH(d.[TechnicalName]),
                       CAST(d.[EditedBy] AS varbinary(400)), DATALENGTH(d.[EditedBy]),
                       d.[ActivatedByRequestId],
                       CAST(d.[OwnerAcceptedBy] AS varbinary(400)), DATALENGTH(d.[OwnerAcceptedBy]))))
        THROW 51123, 'Active or superseded entitlement versions are immutable; an active version can only be superseded (DL-087).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        WHERE i.[State] <> N'Draft' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND (i.[State] <> N'Active' OR NOT EXISTS (
            SELECT 1 FROM [catalog].[EntitlementChangeRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[ActivatedByRequestId] AND r.[VersionId] = i.[Id] AND r.[State] = N'Approved'
            AND r.[EntitlementId] = i.[EntitlementId] AND r.[VersionNumber] = i.[VersionNumber] AND r.[ContentHash] = i.[ContentHash]
            AND r.[OwnerKind] = i.[OwnerKind] AND r.[OwnerId] = i.[OwnerId]
            AND (r.[AcceptedBy] IS NULL OR r.[AcceptedBy] = i.[OwnerAcceptedBy])
            AND r.[Checker] <> i.[EditedBy] AND (i.[OwnerAcceptedBy] IS NULL OR r.[Checker] <> i.[OwnerAcceptedBy]))))
        THROW 51124, 'An entitlement version becomes active only by an approved request naming it, decided by someone who neither wrote it nor accepted it (DL-087).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores the merged trigger exactly, gap included; only for rolling back this migration (DL-020).
            migrationBuilder.Sql(@"ALTER TRIGGER [catalog].[TR_EntitlementVersion_Immutable] ON [catalog].[EntitlementVersion]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([EntitlementId]) OR UPDATE([VersionNumber]))
        THROW 51122, 'Entitlement version Id, EntitlementId and VersionNumber are immutable (DL-087).', 1;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51123, 'Active or superseded entitlement versions are immutable (DL-087).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE d.[State] <> N'Draft' AND NOT (
            d.[State] = N'Active' AND i.[State] = N'Superseded'
            AND i.[ContentHash] = d.[ContentHash] AND i.[OwnerKind] = d.[OwnerKind] AND i.[OwnerId] = d.[OwnerId] AND i.[Privileged] = d.[Privileged]
            AND i.[AllowedAccountClasses] = d.[AllowedAccountClasses] AND i.[TechnicalName] = d.[TechnicalName] AND i.[EditedBy] = d.[EditedBy]
            AND i.[ActivatedByRequestId] = d.[ActivatedByRequestId]
            AND ((i.[OwnerAcceptedBy] IS NULL AND d.[OwnerAcceptedBy] IS NULL) OR i.[OwnerAcceptedBy] = d.[OwnerAcceptedBy])))
        THROW 51123, 'Active or superseded entitlement versions are immutable; an active version can only be superseded (DL-087).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        WHERE i.[State] <> N'Draft' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND (i.[State] <> N'Active' OR NOT EXISTS (
            SELECT 1 FROM [catalog].[EntitlementChangeRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[ActivatedByRequestId] AND r.[VersionId] = i.[Id] AND r.[State] = N'Approved'
            AND r.[EntitlementId] = i.[EntitlementId] AND r.[VersionNumber] = i.[VersionNumber] AND r.[ContentHash] = i.[ContentHash]
            AND r.[OwnerKind] = i.[OwnerKind] AND r.[OwnerId] = i.[OwnerId]
            AND (r.[AcceptedBy] IS NULL OR r.[AcceptedBy] = i.[OwnerAcceptedBy])
            AND r.[Checker] <> i.[EditedBy] AND (i.[OwnerAcceptedBy] IS NULL OR r.[Checker] <> i.[OwnerAcceptedBy]))))
        THROW 51124, 'An entitlement version becomes active only by an approved request naming it, decided by someone who neither wrote it nor accepted it (DL-087).', 1;
END;");
        }
    }
}
