using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class ApplicationCatalogHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-067, hand-written and reviewed (not generated). Review fixes for the application catalog: a person named as
            // a role holder in the settings can't be the one who approved their activation; PostgreSQL also refuses TRUNCATE
            // of settings versions and their roles (SQL Server TRUNCATE never fires triggers and needs ALTER, which only the
            // schema owner has: recorded in R-034).
            migrationBuilder.Sql(@"ALTER TRIGGER [catalog].[TR_ApplicationSettingsVersion_Immutable] ON [catalog].[ApplicationSettingsVersion]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([ApplicationId]) OR UPDATE([VersionNumber]))
        THROW 51107, 'Application settings version Id, ApplicationId and VersionNumber are immutable (DL-066).', 1;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51108, 'Active or superseded application settings versions are immutable (DL-066).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE d.[State] <> N'Draft' AND NOT (
            d.[State] = N'Active' AND i.[State] = N'Superseded'
            AND i.[Owner] = d.[Owner] AND i.[ContentHash] = d.[ContentHash] AND i.[ActivatedByRequestId] = d.[ActivatedByRequestId]))
        THROW 51108, 'Active or superseded application settings versions are immutable; an active version can only be superseded (DL-066).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        WHERE i.[State] <> N'Draft' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND (i.[State] <> N'Active' OR NOT EXISTS (
            SELECT 1 FROM [catalog].[ApplicationSettingsRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[ActivatedByRequestId] AND r.[SettingsVersionId] = i.[Id] AND r.[State] = N'Approved'
            AND r.[ApplicationId] = i.[ApplicationId] AND r.[VersionNumber] = i.[VersionNumber]
            AND r.[ContentHash] = i.[ContentHash] AND r.[ProposedOwner] = i.[Owner])))
        THROW 51109, 'Application settings become active only by an approved request naming them (DL-066).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        JOIN [catalog].[ApplicationSettingsRole] x WITH (READCOMMITTEDLOCK) ON x.[SettingsVersionId] = i.[Id]
        WHERE i.[State] = N'Active' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND x.[Requirement] = N'Required' AND x.[HolderId] IS NULL)
        THROW 51114, 'An application with an empty required role cannot be activated (AR-2.3, DL-052).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        JOIN [catalog].[ApplicationSettingsRole] x WITH (READCOMMITTEDLOCK) ON x.[SettingsVersionId] = i.[Id]
        JOIN [catalog].[ApplicationRoleType] t WITH (READCOMMITTEDLOCK) ON t.[Id] = x.[RoleTypeId]
        WHERE i.[State] = N'Active' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND t.[State] NOT IN (N'BuiltIn', N'Approved'))
        THROW 51115, 'Only built-in or approved role types can be used in active application settings (DL-066).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        JOIN [catalog].[ApplicationSettingsRequest] r WITH (READCOMMITTEDLOCK) ON r.[Id] = i.[ActivatedByRequestId]
        JOIN [catalog].[ApplicationSettingsRole] x WITH (READCOMMITTEDLOCK) ON x.[SettingsVersionId] = i.[Id]
        WHERE i.[State] = N'Active' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND x.[HolderKind] = N'Person' AND x.[HolderId] = r.[Checker])
        THROW 51116, 'A person named as a role holder cannot approve those application settings (DL-067).', 1;
END;");

            migrationBuilder.DropIndex(
                name: "IX_ApplicationRoleType_Code",
                schema: "catalog",
                table: "ApplicationRoleType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores the ApplicationCatalog (DL-066) guards.
            migrationBuilder.Sql(@"ALTER TRIGGER [catalog].[TR_ApplicationSettingsVersion_Immutable] ON [catalog].[ApplicationSettingsVersion]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([ApplicationId]) OR UPDATE([VersionNumber]))
        THROW 51107, 'Application settings version Id, ApplicationId and VersionNumber are immutable (DL-066).', 1;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51108, 'Active or superseded application settings versions are immutable (DL-066).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE d.[State] <> N'Draft' AND NOT (
            d.[State] = N'Active' AND i.[State] = N'Superseded'
            AND i.[Owner] = d.[Owner] AND i.[ContentHash] = d.[ContentHash] AND i.[ActivatedByRequestId] = d.[ActivatedByRequestId]))
        THROW 51108, 'Active or superseded application settings versions are immutable; an active version can only be superseded (DL-066).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        WHERE i.[State] <> N'Draft' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND (i.[State] <> N'Active' OR NOT EXISTS (
            SELECT 1 FROM [catalog].[ApplicationSettingsRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[ActivatedByRequestId] AND r.[SettingsVersionId] = i.[Id] AND r.[State] = N'Approved'
            AND r.[ApplicationId] = i.[ApplicationId] AND r.[VersionNumber] = i.[VersionNumber]
            AND r.[ContentHash] = i.[ContentHash] AND r.[ProposedOwner] = i.[Owner])))
        THROW 51109, 'Application settings become active only by an approved request naming them (DL-066).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        JOIN [catalog].[ApplicationSettingsRole] x WITH (READCOMMITTEDLOCK) ON x.[SettingsVersionId] = i.[Id]
        WHERE i.[State] = N'Active' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND x.[Requirement] = N'Required' AND x.[HolderId] IS NULL)
        THROW 51114, 'An application with an empty required role cannot be activated (AR-2.3, DL-052).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        JOIN [catalog].[ApplicationSettingsRole] x WITH (READCOMMITTEDLOCK) ON x.[SettingsVersionId] = i.[Id]
        JOIN [catalog].[ApplicationRoleType] t WITH (READCOMMITTEDLOCK) ON t.[Id] = x.[RoleTypeId]
        WHERE i.[State] = N'Active' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND t.[State] NOT IN (N'BuiltIn', N'Approved'))
        THROW 51115, 'Only built-in or approved role types can be used in active application settings (DL-066).', 1;
END;");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationRoleType_Code",
                schema: "catalog",
                table: "ApplicationRoleType",
                column: "Code");
        }
    }
}
