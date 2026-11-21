using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Account
{
    /// <inheritdoc />
    public partial class AccountsCreated : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProvisioningItemId",
                schema: "account",
                table: "Account",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Account_ProvisioningItemId",
                schema: "account",
                table: "Account",
                column: "ProvisioningItemId",
                unique: true,
                filter: "[ProvisioningItemId] IS NOT NULL");
            // DL-094, hand-written and reviewed (not generated). An account is recorded either by an approved link request (unverified,
            // as before) or by provisioning once the target confirmed it: origin Created, active, verified, from one provisioning item.
            // The database can't see the provisioning schema, so it checks the shape; who may write it is the account login (R-040).
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] DROP CONSTRAINT [CK_Account_Origin];");
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] ADD CONSTRAINT [CK_Account_Origin] CHECK (([Origin] = N'Linked' AND [LinkRequestId] IS NOT NULL AND [ProvisioningItemId] IS NULL) OR ([Origin] = N'Created' AND [LinkRequestId] IS NULL AND [ProvisioningItemId] IS NOT NULL));");
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
            OR ISNULL(CAST(i.[LinkRequestId] AS NVARCHAR(36)), N'') <> ISNULL(CAST(d.[LinkRequestId] AS NVARCHAR(36)), N''))
        OR (EXISTS (SELECT 1 FROM deleted) AND UPDATE([Id]))
        THROW 51202, 'An account''s application, type, owner and origin are immutable (DL-089).', 1;
    IF NOT EXISTS (SELECT 1 FROM deleted) AND EXISTS (
        SELECT 1 FROM inserted i
        WHERE NOT (i.[Origin] = N'Linked' AND i.[Status] = N'Active' AND i.[Verified] = 0 AND EXISTS (
            SELECT 1 FROM [account].[AccountLinkRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[LinkRequestId] AND r.[State] = N'Approved' AND r.[ApplicationId] = i.[ApplicationId] AND r.[Type] = i.[Type]
              AND r.[Name] = i.[Name] AND r.[TargetId] = i.[TargetId] AND r.[Owner] = i.[Owner])))
        THROW 51203, 'An account is recorded only by an approved link request naming it exactly, unverified (DL-089).', 1;
END;");
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] DROP CONSTRAINT [CK_Account_Origin];");
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] ADD CONSTRAINT [CK_Account_Origin] CHECK (([Origin] = N'Linked' AND [LinkRequestId] IS NOT NULL) OR ([Origin] = N'Created' AND [LinkRequestId] IS NULL));");
            migrationBuilder.DropIndex(
                name: "IX_Account_ProvisioningItemId",
                schema: "account",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "ProvisioningItemId",
                schema: "account",
                table: "Account");
        }
    }
}
