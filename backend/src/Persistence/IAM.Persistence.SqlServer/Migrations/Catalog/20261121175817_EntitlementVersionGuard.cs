using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class EntitlementVersionGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-098, hand-written and reviewed (not generated). Retiring an active version may change its state only: every column
            // its content hash covers, and its owner's acceptance and creation, stay as they were (the stage 8 review found Type,
            // Description, EscalationWorkgroup, Risk and the AccessFor* columns could change). Same comparison rules as DL-095.
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
                SELECT CAST(i.[Type] AS varbinary(64)), DATALENGTH(i.[Type]),
                       CAST(i.[TechnicalName] AS varbinary(512)), DATALENGTH(i.[TechnicalName]),
                       CAST(i.[Description] AS varbinary(4000)), DATALENGTH(i.[Description]),
                       CAST(i.[OwnerKind] AS varbinary(32)), DATALENGTH(i.[OwnerKind]),
                       CAST(i.[OwnerId] AS varbinary(400)), DATALENGTH(i.[OwnerId]),
                       CAST(i.[EscalationWorkgroup] AS varbinary(128)), DATALENGTH(i.[EscalationWorkgroup]),
                       i.[Privileged],
                       CAST(i.[Risk] AS varbinary(32)), DATALENGTH(i.[Risk]),
                       CAST(i.[AllowedAccountClasses] AS varbinary(128)), DATALENGTH(i.[AllowedAccountClasses]),
                       CAST(i.[AccessForMode] AS varbinary(64)), DATALENGTH(i.[AccessForMode]),
                       i.[AccessForDays],
                       i.[AccessForDefaultDays],
                       i.[AccessForMaxDays],
                       CAST(i.[ContentHash] AS varbinary(128)), DATALENGTH(i.[ContentHash]),
                       CAST(i.[EditedBy] AS varbinary(400)), DATALENGTH(i.[EditedBy]),
                       CAST(i.[OwnerAcceptedBy] AS varbinary(400)), DATALENGTH(i.[OwnerAcceptedBy]),
                       i.[OwnerAcceptedAtUtc],
                       i.[ActivatedByRequestId],
                       i.[CreatedAtUtc],
                       CAST(i.[CreatedBy] AS varbinary(400)), DATALENGTH(i.[CreatedBy])
                INTERSECT
                SELECT CAST(d.[Type] AS varbinary(64)), DATALENGTH(d.[Type]),
                       CAST(d.[TechnicalName] AS varbinary(512)), DATALENGTH(d.[TechnicalName]),
                       CAST(d.[Description] AS varbinary(4000)), DATALENGTH(d.[Description]),
                       CAST(d.[OwnerKind] AS varbinary(32)), DATALENGTH(d.[OwnerKind]),
                       CAST(d.[OwnerId] AS varbinary(400)), DATALENGTH(d.[OwnerId]),
                       CAST(d.[EscalationWorkgroup] AS varbinary(128)), DATALENGTH(d.[EscalationWorkgroup]),
                       d.[Privileged],
                       CAST(d.[Risk] AS varbinary(32)), DATALENGTH(d.[Risk]),
                       CAST(d.[AllowedAccountClasses] AS varbinary(128)), DATALENGTH(d.[AllowedAccountClasses]),
                       CAST(d.[AccessForMode] AS varbinary(64)), DATALENGTH(d.[AccessForMode]),
                       d.[AccessForDays],
                       d.[AccessForDefaultDays],
                       d.[AccessForMaxDays],
                       CAST(d.[ContentHash] AS varbinary(128)), DATALENGTH(d.[ContentHash]),
                       CAST(d.[EditedBy] AS varbinary(400)), DATALENGTH(d.[EditedBy]),
                       CAST(d.[OwnerAcceptedBy] AS varbinary(400)), DATALENGTH(d.[OwnerAcceptedBy]),
                       d.[OwnerAcceptedAtUtc],
                       d.[ActivatedByRequestId],
                       d.[CreatedAtUtc],
                       CAST(d.[CreatedBy] AS varbinary(400)), DATALENGTH(d.[CreatedBy]))))
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
    }
}
