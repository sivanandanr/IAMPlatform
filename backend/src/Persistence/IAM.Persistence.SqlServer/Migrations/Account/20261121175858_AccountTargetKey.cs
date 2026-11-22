using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Account
{
    /// <inheritdoc />
    public partial class AccountTargetKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetIdKey",
                schema: "account",
                table: "Account",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Account_TargetIdKey_Code",
                schema: "account",
                table: "Account",
                sql: "DATALENGTH([TargetIdKey]) > 0 AND DATALENGTH([TargetIdKey]) = DATALENGTH(LTRIM(RTRIM([TargetIdKey])))");
            // DL-098, hand-written and reviewed (not generated). An account's target id is unique among the application's live
            // accounts ignoring case, on both engines (directories compare DNs that way); its name and target id never change after
            // it is recorded; a retired naming-rule template is compared exactly on SQL Server, as on PostgreSQL.
            migrationBuilder.Sql(@"UPDATE [account].[Account] SET [TargetIdKey] = LOWER([TargetId]) WHERE [TargetId] IS NOT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] ADD CONSTRAINT [CK_Account_TargetIdKey] CHECK (([TargetId] IS NULL AND [TargetIdKey] IS NULL) OR ([TargetId] IS NOT NULL AND [TargetIdKey] IS NOT NULL AND CAST([TargetIdKey] AS VARBINARY(1024)) = CAST(LOWER([TargetId]) AS VARBINARY(1024))));");
            migrationBuilder.Sql(@"DROP INDEX [UX_Account_LiveTarget] ON [account].[Account];");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_Account_LiveTargetKey] ON [account].[Account] ([ApplicationId], [TargetIdKey]) WHERE [TargetIdKey] IS NOT NULL AND [Status] <> N'Closed';");
            migrationBuilder.Sql(@"ALTER TRIGGER [account].[TR_Account_Guarded] ON [account].[Account]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51201, 'account.Account rows cannot be deleted (DL-089).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE i.[ApplicationId] <> d.[ApplicationId] OR i.[Type] <> d.[Type] OR i.[Owner] <> d.[Owner] OR i.[Origin] <> d.[Origin]
            OR ISNULL(CAST(i.[LinkRequestId] AS NVARCHAR(36)), N'') <> ISNULL(CAST(d.[LinkRequestId] AS NVARCHAR(36)), N'')
            OR NOT EXISTS (SELECT i.[ProvisioningItemId] INTERSECT SELECT d.[ProvisioningItemId])
            OR NOT EXISTS (
                SELECT CAST(i.[Name] AS varbinary(512)), DATALENGTH(i.[Name]),
                       CAST(i.[NameKey] AS varbinary(512)), DATALENGTH(i.[NameKey]),
                       CAST(i.[TargetId] AS varbinary(1024)), DATALENGTH(i.[TargetId]),
                       CAST(i.[TargetIdKey] AS varbinary(1024)), DATALENGTH(i.[TargetIdKey])
                INTERSECT
                SELECT CAST(d.[Name] AS varbinary(512)), DATALENGTH(d.[Name]),
                       CAST(d.[NameKey] AS varbinary(512)), DATALENGTH(d.[NameKey]),
                       CAST(d.[TargetId] AS varbinary(1024)), DATALENGTH(d.[TargetId]),
                       CAST(d.[TargetIdKey] AS varbinary(1024)), DATALENGTH(d.[TargetIdKey])))
        OR (EXISTS (SELECT 1 FROM deleted) AND UPDATE([Id]))
        THROW 51202, 'An account''s application, type, owner, origin, name and target id are immutable (DL-089, DL-098).', 1;
    IF NOT EXISTS (SELECT 1 FROM deleted) AND EXISTS (
        SELECT 1 FROM inserted i
        WHERE NOT (i.[Origin] = N'Linked' AND i.[Status] = N'Active' AND i.[Verified] = 0 AND EXISTS (
            SELECT 1 FROM [account].[AccountLinkRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[LinkRequestId] AND r.[State] = N'Approved' AND r.[ApplicationId] = i.[ApplicationId] AND r.[Type] = i.[Type]
              AND r.[Name] = i.[Name] AND r.[TargetId] = i.[TargetId] AND r.[Owner] = i.[Owner]))
            AND NOT (i.[Origin] = N'Created' AND i.[Status] = N'Active' AND i.[Verified] = 1 AND i.[ProvisioningItemId] IS NOT NULL AND i.[TargetId] IS NOT NULL))
        THROW 51203, 'An account is recorded only by an approved link request naming it exactly, unverified, or by provisioning once the target confirmed it (DL-089, DL-094).', 1;
END;");
            migrationBuilder.Sql(@"ALTER TRIGGER [account].[TR_NamingRuleVersion_Immutable] ON [account].[NamingRuleVersion]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([NamingRuleId]) OR UPDATE([VersionNumber]))
        THROW 51209, 'Naming rule version Id, rule and number are immutable (DL-089).', 1;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51210, 'Active or superseded naming rule versions are immutable (DL-089).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE d.[State] <> N'Draft' AND NOT (
            d.[State] = N'Active' AND i.[State] = N'Superseded' AND CAST(i.[Template] AS varbinary(max)) = CAST(d.[Template] AS varbinary(max)) AND DATALENGTH(i.[Template]) = DATALENGTH(d.[Template]) AND i.[ContentHash] = d.[ContentHash]
            AND i.[EditedBy] = d.[EditedBy] AND i.[ActivatedByRequestId] = d.[ActivatedByRequestId]))
        THROW 51210, 'Active or superseded naming rule versions are immutable; an active version can only be superseded (DL-089).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        WHERE i.[State] <> N'Draft' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND (i.[State] <> N'Active' OR NOT EXISTS (
            SELECT 1 FROM [account].[NamingRuleChangeRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[ActivatedByRequestId] AND r.[VersionId] = i.[Id] AND r.[State] = N'Approved'
            AND r.[NamingRuleId] = i.[NamingRuleId] AND r.[VersionNumber] = i.[VersionNumber] AND r.[ContentHash] = i.[ContentHash]
            AND r.[Checker] <> i.[EditedBy])))
        THROW 51211, 'A naming rule version becomes active only by an approved request naming it, decided by someone who did not write it (DL-089).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TRIGGER [account].[TR_NamingRuleVersion_Immutable] ON [account].[NamingRuleVersion]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([NamingRuleId]) OR UPDATE([VersionNumber]))
        THROW 51209, 'Naming rule version Id, rule and number are immutable (DL-089).', 1;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51210, 'Active or superseded naming rule versions are immutable (DL-089).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE d.[State] <> N'Draft' AND NOT (
            d.[State] = N'Active' AND i.[State] = N'Superseded' AND i.[Template] = d.[Template] AND i.[ContentHash] = d.[ContentHash]
            AND i.[EditedBy] = d.[EditedBy] AND i.[ActivatedByRequestId] = d.[ActivatedByRequestId]))
        THROW 51210, 'Active or superseded naming rule versions are immutable; an active version can only be superseded (DL-089).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        WHERE i.[State] <> N'Draft' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND (i.[State] <> N'Active' OR NOT EXISTS (
            SELECT 1 FROM [account].[NamingRuleChangeRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[ActivatedByRequestId] AND r.[VersionId] = i.[Id] AND r.[State] = N'Approved'
            AND r.[NamingRuleId] = i.[NamingRuleId] AND r.[VersionNumber] = i.[VersionNumber] AND r.[ContentHash] = i.[ContentHash]
            AND r.[Checker] <> i.[EditedBy])))
        THROW 51211, 'A naming rule version becomes active only by an approved request naming it, decided by someone who did not write it (DL-089).', 1;
END;");
            migrationBuilder.Sql(@"ALTER TRIGGER [account].[TR_Account_Guarded] ON [account].[Account]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51201, 'account.Account rows cannot be deleted (DL-089).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE i.[ApplicationId] <> d.[ApplicationId] OR i.[Type] <> d.[Type] OR i.[Owner] <> d.[Owner] OR i.[Origin] <> d.[Origin]
            OR ISNULL(CAST(i.[LinkRequestId] AS NVARCHAR(36)), N'') <> ISNULL(CAST(d.[LinkRequestId] AS NVARCHAR(36)), N'')
            OR NOT EXISTS (SELECT i.[ProvisioningItemId] INTERSECT SELECT d.[ProvisioningItemId]))
        OR (EXISTS (SELECT 1 FROM deleted) AND UPDATE([Id]))
        THROW 51202, 'An account''s application, type, owner and origin are immutable (DL-089).', 1;
    IF NOT EXISTS (SELECT 1 FROM deleted) AND EXISTS (
        SELECT 1 FROM inserted i
        WHERE NOT (i.[Origin] = N'Linked' AND i.[Status] = N'Active' AND i.[Verified] = 0 AND EXISTS (
            SELECT 1 FROM [account].[AccountLinkRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[LinkRequestId] AND r.[State] = N'Approved' AND r.[ApplicationId] = i.[ApplicationId] AND r.[Type] = i.[Type]
              AND r.[Name] = i.[Name] AND r.[TargetId] = i.[TargetId] AND r.[Owner] = i.[Owner]))
            AND NOT (i.[Origin] = N'Created' AND i.[Status] = N'Active' AND i.[Verified] = 1 AND i.[ProvisioningItemId] IS NOT NULL AND i.[TargetId] IS NOT NULL))
        THROW 51203, 'An account is recorded only by an approved link request naming it exactly, unverified, or by provisioning once the target confirmed it (DL-089, DL-094).', 1;
END;");
            migrationBuilder.Sql(@"DROP INDEX [UX_Account_LiveTargetKey] ON [account].[Account];");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_Account_LiveTarget] ON [account].[Account] ([ApplicationId], [TargetId]) WHERE [TargetId] IS NOT NULL AND [Status] <> N'Closed';");
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] DROP CONSTRAINT [CK_Account_TargetIdKey];");
            migrationBuilder.DropCheckConstraint(
                name: "CK_Account_TargetIdKey_Code",
                schema: "account",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "TargetIdKey",
                schema: "account",
                table: "Account");
        }
    }
}
