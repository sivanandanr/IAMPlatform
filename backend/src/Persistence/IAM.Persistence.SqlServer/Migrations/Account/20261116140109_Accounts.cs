using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Account
{
    /// <inheritdoc />
    public partial class Accounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "account");

            migrationBuilder.CreateTable(
                name: "AccountLinkRequest",
                schema: "account",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    NameKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TargetId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Owner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Maker = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ProposedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Checker = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2"),
                    DecisionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountLinkRequest", x => x.Id);
                    table.CheckConstraint("CK_AccountLinkRequest_Checker_Code", "DATALENGTH([Checker]) > 0 AND DATALENGTH([Checker]) = DATALENGTH(LTRIM(RTRIM([Checker])))");
                    table.CheckConstraint("CK_AccountLinkRequest_Maker_Code", "DATALENGTH([Maker]) > 0 AND DATALENGTH([Maker]) = DATALENGTH(LTRIM(RTRIM([Maker])))");
                    table.CheckConstraint("CK_AccountLinkRequest_Name_Code", "DATALENGTH([Name]) > 0 AND DATALENGTH([Name]) = DATALENGTH(LTRIM(RTRIM([Name])))");
                    table.CheckConstraint("CK_AccountLinkRequest_NameKey_Code", "DATALENGTH([NameKey]) > 0 AND DATALENGTH([NameKey]) = DATALENGTH(LTRIM(RTRIM([NameKey])))");
                    table.CheckConstraint("CK_AccountLinkRequest_Owner_Code", "DATALENGTH([Owner]) > 0 AND DATALENGTH([Owner]) = DATALENGTH(LTRIM(RTRIM([Owner])))");
                    table.CheckConstraint("CK_AccountLinkRequest_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.CheckConstraint("CK_AccountLinkRequest_Type_Code", "DATALENGTH([Type]) > 0 AND DATALENGTH([Type]) = DATALENGTH(LTRIM(RTRIM([Type])))");
                });

            migrationBuilder.CreateTable(
                name: "NamingRule",
                schema: "account",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NamingRule", x => x.Id);
                    table.CheckConstraint("CK_NamingRule_AccountType_Code", "DATALENGTH([AccountType]) > 0 AND DATALENGTH([AccountType]) = DATALENGTH(LTRIM(RTRIM([AccountType])))");
                });

            migrationBuilder.CreateTable(
                name: "Account",
                schema: "account",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    NameKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TargetId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Owner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Origin = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    LinkRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Verified = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Account", x => x.Id);
                    table.CheckConstraint("CK_Account_Name_Code", "DATALENGTH([Name]) > 0 AND DATALENGTH([Name]) = DATALENGTH(LTRIM(RTRIM([Name])))");
                    table.CheckConstraint("CK_Account_NameKey_Code", "DATALENGTH([NameKey]) > 0 AND DATALENGTH([NameKey]) = DATALENGTH(LTRIM(RTRIM([NameKey])))");
                    table.CheckConstraint("CK_Account_Origin_Code", "DATALENGTH([Origin]) > 0 AND DATALENGTH([Origin]) = DATALENGTH(LTRIM(RTRIM([Origin])))");
                    table.CheckConstraint("CK_Account_Owner_Code", "DATALENGTH([Owner]) > 0 AND DATALENGTH([Owner]) = DATALENGTH(LTRIM(RTRIM([Owner])))");
                    table.CheckConstraint("CK_Account_Status_Code", "DATALENGTH([Status]) > 0 AND DATALENGTH([Status]) = DATALENGTH(LTRIM(RTRIM([Status])))");
                    table.CheckConstraint("CK_Account_Type_Code", "DATALENGTH([Type]) > 0 AND DATALENGTH([Type]) = DATALENGTH(LTRIM(RTRIM([Type])))");
                    table.ForeignKey(
                        name: "FK_Account_AccountLinkRequest_LinkRequestId",
                        column: x => x.LinkRequestId,
                        principalSchema: "account",
                        principalTable: "AccountLinkRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NamingRuleChangeRequest",
                schema: "account",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NamingRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Maker = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ProposedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Checker = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2"),
                    DecisionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NamingRuleChangeRequest", x => x.Id);
                    table.CheckConstraint("CK_NamingRuleChangeRequest_Checker_Code", "DATALENGTH([Checker]) > 0 AND DATALENGTH([Checker]) = DATALENGTH(LTRIM(RTRIM([Checker])))");
                    table.CheckConstraint("CK_NamingRuleChangeRequest_ContentHash_Code", "DATALENGTH([ContentHash]) > 0 AND DATALENGTH([ContentHash]) = DATALENGTH(LTRIM(RTRIM([ContentHash])))");
                    table.CheckConstraint("CK_NamingRuleChangeRequest_Maker_Code", "DATALENGTH([Maker]) > 0 AND DATALENGTH([Maker]) = DATALENGTH(LTRIM(RTRIM([Maker])))");
                    table.CheckConstraint("CK_NamingRuleChangeRequest_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.ForeignKey(
                        name: "FK_NamingRuleChangeRequest_NamingRule_NamingRuleId",
                        column: x => x.NamingRuleId,
                        principalSchema: "account",
                        principalTable: "NamingRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NamingRuleVersion",
                schema: "account",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NamingRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Template = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    EditedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActivatedByRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NamingRuleVersion", x => x.Id);
                    table.CheckConstraint("CK_NamingRuleVersion_ContentHash_Code", "DATALENGTH([ContentHash]) > 0 AND DATALENGTH([ContentHash]) = DATALENGTH(LTRIM(RTRIM([ContentHash])))");
                    table.CheckConstraint("CK_NamingRuleVersion_EditedBy_Code", "DATALENGTH([EditedBy]) > 0 AND DATALENGTH([EditedBy]) = DATALENGTH(LTRIM(RTRIM([EditedBy])))");
                    table.CheckConstraint("CK_NamingRuleVersion_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.ForeignKey(
                        name: "FK_NamingRuleVersion_NamingRuleChangeRequest_ActivatedByRequestId",
                        column: x => x.ActivatedByRequestId,
                        principalSchema: "account",
                        principalTable: "NamingRuleChangeRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NamingRuleVersion_NamingRule_NamingRuleId",
                        column: x => x.NamingRuleId,
                        principalSchema: "account",
                        principalTable: "NamingRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Account_ApplicationId_Owner",
                schema: "account",
                table: "Account",
                columns: new[] { "ApplicationId", "Owner" });

            migrationBuilder.CreateIndex(
                name: "IX_Account_LinkRequestId",
                schema: "account",
                table: "Account",
                column: "LinkRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountLinkRequest_State_ProposedAtUtc",
                schema: "account",
                table: "AccountLinkRequest",
                columns: new[] { "State", "ProposedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NamingRule_ApplicationId_AccountType",
                schema: "account",
                table: "NamingRule",
                columns: new[] { "ApplicationId", "AccountType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NamingRuleChangeRequest_NamingRuleId",
                schema: "account",
                table: "NamingRuleChangeRequest",
                column: "NamingRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_NamingRuleChangeRequest_State_ProposedAtUtc",
                schema: "account",
                table: "NamingRuleChangeRequest",
                columns: new[] { "State", "ProposedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NamingRuleChangeRequest_VersionId",
                schema: "account",
                table: "NamingRuleChangeRequest",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_NamingRuleVersion_ActivatedByRequestId",
                schema: "account",
                table: "NamingRuleVersion",
                column: "ActivatedByRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_NamingRuleVersion_NamingRuleId_VersionNumber",
                schema: "account",
                table: "NamingRuleVersion",
                columns: new[] { "NamingRuleId", "VersionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_NamingRuleChangeRequest_NamingRuleVersion_VersionId",
                schema: "account",
                table: "NamingRuleChangeRequest",
                column: "VersionId",
                principalSchema: "account",
                principalTable: "NamingRuleVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            // DL-089, hand-written and reviewed (not generated). Accounts are never deleted; who an account belongs to, its type,
            // application and origin never change. In this slice an account is recorded only by an approved link request naming it
            // exactly, decided by someone other than the maker or the account's owner. One live Primary per person per
            // application; live names (ignoring case) and target ids are unique per application. Naming rule versions become
            // active only by an approved request naming them, decided by someone other than the content's author; active and
            // superseded versions and decided requests never change. Trigger names match the HasTrigger declarations in
            // AccountDbContext.
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] ADD CONSTRAINT [CK_Account_Codes] CHECK ([Type] IN (N'Primary', N'Secondary', N'Service', N'Shared') AND [Status] IN (N'Requested', N'Active', N'Disabled', N'Closed') AND [Origin] IN (N'Linked', N'Created'));");
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] ADD CONSTRAINT [CK_Account_Name] CHECK (LEN([Name]) BETWEEN 1 AND 256 AND DATALENGTH([Name]) = 2 * LEN([Name]) AND [Name] NOT LIKE '[._-]%' AND [Name] NOT LIKE '%[^A-Za-z0-9._-]%' AND CAST([NameKey] AS VARBINARY(512)) = CAST(LOWER([Name]) AS VARBINARY(512)));");
            migrationBuilder.Sql(@"ALTER TABLE [account].[Account] ADD CONSTRAINT [CK_Account_Origin] CHECK (([Origin] = N'Linked' AND [LinkRequestId] IS NOT NULL) OR ([Origin] = N'Created' AND [LinkRequestId] IS NULL));");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_Account_OnePrimary] ON [account].[Account] ([ApplicationId], [Owner]) WHERE [Type] = N'Primary' AND [Status] <> N'Closed';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_Account_LiveName] ON [account].[Account] ([ApplicationId], [NameKey]) WHERE [Status] <> N'Closed';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_Account_LiveTarget] ON [account].[Account] ([ApplicationId], [TargetId]) WHERE [TargetId] IS NOT NULL AND [Status] <> N'Closed';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_Account_LinkRequest] ON [account].[Account] ([LinkRequestId]) WHERE [LinkRequestId] IS NOT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE [account].[AccountLinkRequest] ADD CONSTRAINT [CK_AccountLinkRequest_State] CHECK (
    ([State] = N'Proposed' AND [Checker] IS NULL AND [DecisionReason] IS NULL AND [DecidedAtUtc] IS NULL)
    OR ([State] = N'Withdrawn' AND [Checker] IS NOT NULL AND [Checker] = [Maker] AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] = N'Rejected' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [Checker] <> [Owner] AND [DecisionReason] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] = N'Approved' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [Checker] <> [Owner] AND [DecidedAtUtc] IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE [account].[AccountLinkRequest] ADD CONSTRAINT [CK_AccountLinkRequest_Codes] CHECK ([Type] IN (N'Primary', N'Secondary', N'Service', N'Shared') AND CAST([NameKey] AS VARBINARY(512)) = CAST(LOWER([Name]) AS VARBINARY(512)));");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_AccountLinkRequest_OnePendingName] ON [account].[AccountLinkRequest] ([ApplicationId], [NameKey]) WHERE [State] = N'Proposed';");
            migrationBuilder.Sql(@"ALTER TABLE [account].[NamingRule] ADD CONSTRAINT [CK_NamingRule_AccountType] CHECK ([AccountType] IN (N'Primary', N'Secondary'));");
            migrationBuilder.Sql(@"ALTER TABLE [account].[NamingRuleVersion] ADD CONSTRAINT [CK_NamingRuleVersion_State] CHECK (
    ([State] = N'Draft' AND [ActivatedByRequestId] IS NULL)
    OR ([State] IN (N'Active', N'Superseded') AND [ActivatedByRequestId] IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE [account].[NamingRuleVersion] ADD CONSTRAINT [CK_NamingRuleVersion_ContentHash_Hex] CHECK (LEN([ContentHash]) = 64 AND [ContentHash] NOT LIKE '%[^0-9a-f]%' AND [VersionNumber] >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_NamingRuleVersion_OneDraft] ON [account].[NamingRuleVersion] ([NamingRuleId]) WHERE [State] = N'Draft';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_NamingRuleVersion_OneActive] ON [account].[NamingRuleVersion] ([NamingRuleId]) WHERE [State] = N'Active';");
            migrationBuilder.Sql(@"ALTER TABLE [account].[NamingRuleChangeRequest] ADD CONSTRAINT [CK_NamingRuleChangeRequest_State] CHECK (
    ([State] = N'Proposed' AND [Checker] IS NULL AND [DecisionReason] IS NULL AND [DecidedAtUtc] IS NULL)
    OR ([State] = N'Withdrawn' AND [Checker] IS NOT NULL AND [Checker] = [Maker] AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] = N'Rejected' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecisionReason] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] = N'Approved' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecidedAtUtc] IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE [account].[NamingRuleChangeRequest] ADD CONSTRAINT [CK_NamingRuleChangeRequest_ContentHash_Hex] CHECK (LEN([ContentHash]) = 64 AND [ContentHash] NOT LIKE '%[^0-9a-f]%' AND [VersionNumber] >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_NamingRuleChangeRequest_OnePending] ON [account].[NamingRuleChangeRequest] ([NamingRuleId]) WHERE [State] = N'Proposed';");
            migrationBuilder.Sql(@"ALTER TABLE [account].[NamingRuleChangeRequest] ADD CONSTRAINT [AK_NamingRuleChangeRequest_Id_VersionId] UNIQUE ([Id], [VersionId]);");
            migrationBuilder.Sql(@"ALTER TABLE [account].[NamingRuleVersion] ADD CONSTRAINT [FK_NamingRuleVersion_Request_Names] FOREIGN KEY ([ActivatedByRequestId], [Id]) REFERENCES [account].[NamingRuleChangeRequest] ([Id], [VersionId]);");
            migrationBuilder.Sql(@"CREATE TRIGGER [account].[TR_Account_Guarded] ON [account].[Account]
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
            migrationBuilder.Sql(@"CREATE TRIGGER [account].[TR_AccountLinkRequest_DecidedImmutable] ON [account].[AccountLinkRequest]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51204, 'account.AccountLinkRequest rows are immutable and cannot be deleted (DL-089).', 1;
    IF UPDATE([Id]) OR UPDATE([ApplicationId]) OR UPDATE([Type]) OR UPDATE([Name]) OR UPDATE([NameKey]) OR UPDATE([TargetId]) OR UPDATE([Owner]) OR UPDATE([Maker]) OR UPDATE([Reason]) OR UPDATE([ProposedAtUtc])
        THROW 51205, 'account.AccountLinkRequest proposal fields are immutable (DL-089).', 1;
    IF EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Proposed')
        THROW 51206, 'Decided account.AccountLinkRequest rows are immutable (DL-089).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [account].[TR_NamingRule_Immutable] ON [account].[NamingRule]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51207, 'account.NamingRule rows cannot be deleted (DL-089).', 1;
    IF EXISTS (SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id] WHERE i.[ApplicationId] <> d.[ApplicationId] OR i.[AccountType] <> d.[AccountType]) OR UPDATE([Id])
        THROW 51208, 'A naming rule''s application and account type are immutable (DL-089).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [account].[TR_NamingRuleVersion_Immutable] ON [account].[NamingRuleVersion]
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
            migrationBuilder.Sql(@"CREATE TRIGGER [account].[TR_NamingRuleChangeRequest_DecidedImmutable] ON [account].[NamingRuleChangeRequest]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51212, 'account.NamingRuleChangeRequest rows are immutable and cannot be deleted (DL-089).', 1;
    IF UPDATE([Id]) OR UPDATE([NamingRuleId]) OR UPDATE([VersionId]) OR UPDATE([VersionNumber]) OR UPDATE([ContentHash]) OR UPDATE([Maker]) OR UPDATE([Reason]) OR UPDATE([ProposedAtUtc])
        THROW 51213, 'account.NamingRuleChangeRequest proposal fields are immutable (DL-089).', 1;
    IF EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Proposed')
        THROW 51214, 'Decided account.NamingRuleChangeRequest rows are immutable (DL-089).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the account schema's guards; only valid on a database that holds no account data worth keeping (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [account].[TR_NamingRuleChangeRequest_DecidedImmutable];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [account].[TR_NamingRuleVersion_Immutable];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [account].[TR_NamingRule_Immutable];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [account].[TR_AccountLinkRequest_DecidedImmutable];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [account].[TR_Account_Guarded];");
            migrationBuilder.Sql(@"ALTER TABLE [account].[NamingRuleVersion] DROP CONSTRAINT [FK_NamingRuleVersion_Request_Names];");
            migrationBuilder.DropForeignKey(
                name: "FK_NamingRuleChangeRequest_NamingRuleVersion_VersionId",
                schema: "account",
                table: "NamingRuleChangeRequest");

            migrationBuilder.DropTable(
                name: "Account",
                schema: "account");

            migrationBuilder.DropTable(
                name: "AccountLinkRequest",
                schema: "account");

            migrationBuilder.DropTable(
                name: "NamingRuleVersion",
                schema: "account");

            migrationBuilder.DropTable(
                name: "NamingRuleChangeRequest",
                schema: "account");

            migrationBuilder.DropTable(
                name: "NamingRule",
                schema: "account");
        }
    }
}
