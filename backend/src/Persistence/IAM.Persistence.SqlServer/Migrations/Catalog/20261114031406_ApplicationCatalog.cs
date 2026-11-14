using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class ApplicationCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "Application",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Application", x => x.Id);
                    table.CheckConstraint("CK_Application_Key_Code", "DATALENGTH([Key]) > 0 AND DATALENGTH([Key]) = DATALENGTH(LTRIM(RTRIM([Key])))");
                });

            migrationBuilder.CreateTable(
                name: "ApplicationRoleType",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Maker = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProposedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
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
                    table.PrimaryKey("PK_ApplicationRoleType", x => x.Id);
                    table.CheckConstraint("CK_ApplicationRoleType_Checker_Code", "DATALENGTH([Checker]) > 0 AND DATALENGTH([Checker]) = DATALENGTH(LTRIM(RTRIM([Checker])))");
                    table.CheckConstraint("CK_ApplicationRoleType_Code_Code", "DATALENGTH([Code]) > 0 AND DATALENGTH([Code]) = DATALENGTH(LTRIM(RTRIM([Code])))");
                    table.CheckConstraint("CK_ApplicationRoleType_Maker_Code", "DATALENGTH([Maker]) > 0 AND DATALENGTH([Maker]) = DATALENGTH(LTRIM(RTRIM([Maker])))");
                    table.CheckConstraint("CK_ApplicationRoleType_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                });

            migrationBuilder.CreateTable(
                name: "ApplicationSettingsRequest",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SettingsVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProposedOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
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
                    table.PrimaryKey("PK_ApplicationSettingsRequest", x => x.Id);
                    table.CheckConstraint("CK_ApplicationSettingsRequest_Checker_Code", "DATALENGTH([Checker]) > 0 AND DATALENGTH([Checker]) = DATALENGTH(LTRIM(RTRIM([Checker])))");
                    table.CheckConstraint("CK_ApplicationSettingsRequest_ContentHash_Code", "DATALENGTH([ContentHash]) > 0 AND DATALENGTH([ContentHash]) = DATALENGTH(LTRIM(RTRIM([ContentHash])))");
                    table.CheckConstraint("CK_ApplicationSettingsRequest_Maker_Code", "DATALENGTH([Maker]) > 0 AND DATALENGTH([Maker]) = DATALENGTH(LTRIM(RTRIM([Maker])))");
                    table.CheckConstraint("CK_ApplicationSettingsRequest_ProposedOwner_Code", "DATALENGTH([ProposedOwner]) > 0 AND DATALENGTH([ProposedOwner]) = DATALENGTH(LTRIM(RTRIM([ProposedOwner])))");
                    table.CheckConstraint("CK_ApplicationSettingsRequest_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.ForeignKey(
                        name: "FK_ApplicationSettingsRequest_Application_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "catalog",
                        principalTable: "Application",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationSettingsVersion",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Owner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActivatedByRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationSettingsVersion", x => x.Id);
                    table.CheckConstraint("CK_ApplicationSettingsVersion_ContentHash_Code", "DATALENGTH([ContentHash]) > 0 AND DATALENGTH([ContentHash]) = DATALENGTH(LTRIM(RTRIM([ContentHash])))");
                    table.CheckConstraint("CK_ApplicationSettingsVersion_Owner_Code", "DATALENGTH([Owner]) > 0 AND DATALENGTH([Owner]) = DATALENGTH(LTRIM(RTRIM([Owner])))");
                    table.CheckConstraint("CK_ApplicationSettingsVersion_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.ForeignKey(
                        name: "FK_ApplicationSettingsVersion_ApplicationSettingsRequest_ActivatedByRequestId",
                        column: x => x.ActivatedByRequestId,
                        principalSchema: "catalog",
                        principalTable: "ApplicationSettingsRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationSettingsVersion_Application_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "catalog",
                        principalTable: "Application",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationSettingsRole",
                schema: "catalog",
                columns: table => new
                {
                    SettingsVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Requirement = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    HolderKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true, collation: "Latin1_General_100_BIN2"),
                    HolderId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationSettingsRole", x => new { x.SettingsVersionId, x.RoleTypeId });
                    table.CheckConstraint("CK_ApplicationSettingsRole_HolderId_Code", "DATALENGTH([HolderId]) > 0 AND DATALENGTH([HolderId]) = DATALENGTH(LTRIM(RTRIM([HolderId])))");
                    table.CheckConstraint("CK_ApplicationSettingsRole_HolderKind_Code", "DATALENGTH([HolderKind]) > 0 AND DATALENGTH([HolderKind]) = DATALENGTH(LTRIM(RTRIM([HolderKind])))");
                    table.CheckConstraint("CK_ApplicationSettingsRole_Requirement_Code", "DATALENGTH([Requirement]) > 0 AND DATALENGTH([Requirement]) = DATALENGTH(LTRIM(RTRIM([Requirement])))");
                    table.ForeignKey(
                        name: "FK_ApplicationSettingsRole_ApplicationRoleType_RoleTypeId",
                        column: x => x.RoleTypeId,
                        principalSchema: "catalog",
                        principalTable: "ApplicationRoleType",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationSettingsRole_ApplicationSettingsVersion_SettingsVersionId",
                        column: x => x.SettingsVersionId,
                        principalSchema: "catalog",
                        principalTable: "ApplicationSettingsVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Application_Key",
                schema: "catalog",
                table: "Application",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationRoleType_Code",
                schema: "catalog",
                table: "ApplicationRoleType",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationSettingsRequest_ApplicationId",
                schema: "catalog",
                table: "ApplicationSettingsRequest",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationSettingsRequest_SettingsVersionId",
                schema: "catalog",
                table: "ApplicationSettingsRequest",
                column: "SettingsVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationSettingsRequest_State_ProposedAtUtc",
                schema: "catalog",
                table: "ApplicationSettingsRequest",
                columns: new[] { "State", "ProposedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationSettingsRole_RoleTypeId",
                schema: "catalog",
                table: "ApplicationSettingsRole",
                column: "RoleTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationSettingsVersion_ActivatedByRequestId",
                schema: "catalog",
                table: "ApplicationSettingsVersion",
                column: "ActivatedByRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationSettingsVersion_ApplicationId_VersionNumber",
                schema: "catalog",
                table: "ApplicationSettingsVersion",
                columns: new[] { "ApplicationId", "VersionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ApplicationSettingsRequest_ApplicationSettingsVersion_SettingsVersionId",
                schema: "catalog",
                table: "ApplicationSettingsRequest",
                column: "SettingsVersionId",
                principalSchema: "catalog",
                principalTable: "ApplicationSettingsVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            // DL-066, hand-written and reviewed (not generated). Built-in role types are seeded before the guards exist, so no
            // login can add another without a second person. Applications and role types are never deleted; a settings version
            // becomes active only by an approved request naming it, with every required role filled and only usable role
            // types; active and superseded versions and their roles never change, except an active version being superseded.
            // Trigger names match the HasTrigger declarations in CatalogDbContext.
            migrationBuilder.Sql(@"INSERT INTO [catalog].[ApplicationRoleType] ([Id], [Code], [DisplayName], [State], [CreatedAtUtc], [CreatedBy], [ModifiedAtUtc], [ModifiedBy], [Version]) VALUES ('0193a1f0-0000-7000-8000-000000000001', N'user_admin', N'User Admin', N'BuiltIn', '2026-11-14T00:00:00+00:00', N'migration:ApplicationCatalog', '2026-11-14T00:00:00+00:00', N'migration:ApplicationCatalog', 1), ('0193a1f0-0000-7000-8000-000000000002', N'it_admin', N'IT Admin', N'BuiltIn', '2026-11-14T00:00:00+00:00', N'migration:ApplicationCatalog', '2026-11-14T00:00:00+00:00', N'migration:ApplicationCatalog', 1), ('0193a1f0-0000-7000-8000-000000000003', N'technical_custodian', N'Technical custodian', N'BuiltIn', '2026-11-14T00:00:00+00:00', N'migration:ApplicationCatalog', '2026-11-14T00:00:00+00:00', N'migration:ApplicationCatalog', 1);");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[Application] ADD CONSTRAINT [CK_Application_Key_Format] CHECK (LEN([Key]) BETWEEN 2 AND 64 AND DATALENGTH([Key]) = 2 * LEN([Key]) AND [Key] LIKE '[a-z]%' AND [Key] NOT LIKE '%[^a-z0-9_]%');");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationRoleType] ADD CONSTRAINT [CK_ApplicationRoleType_Code_Format] CHECK (LEN([Code]) BETWEEN 2 AND 64 AND DATALENGTH([Code]) = 2 * LEN([Code]) AND [Code] LIKE '[a-z]%' AND [Code] NOT LIKE '%[^a-z0-9_]%');");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationRoleType] ADD CONSTRAINT [CK_ApplicationRoleType_State] CHECK (
    ([State] = N'BuiltIn' AND [Maker] IS NULL AND [Reason] IS NULL AND [ProposedAtUtc] IS NULL AND [Checker] IS NULL AND [DecisionReason] IS NULL AND [DecidedAtUtc] IS NULL)
    OR ([Maker] IS NOT NULL AND [Reason] IS NOT NULL AND [ProposedAtUtc] IS NOT NULL AND (
        ([State] = N'Proposed' AND [Checker] IS NULL AND [DecisionReason] IS NULL AND [DecidedAtUtc] IS NULL)
        OR ([State] = N'Withdrawn' AND [Checker] IS NOT NULL AND [Checker] = [Maker] AND [DecidedAtUtc] IS NOT NULL)
        OR ([State] = N'Rejected' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecisionReason] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL)
        OR ([State] = N'Approved' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecidedAtUtc] IS NOT NULL))));");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_ApplicationRoleType_LiveCode] ON [catalog].[ApplicationRoleType] ([Code]) WHERE [State] IN (N'BuiltIn', N'Proposed', N'Approved');");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsVersion] ADD CONSTRAINT [CK_ApplicationSettingsVersion_State] CHECK (
    ([State] = N'Draft' AND [ActivatedByRequestId] IS NULL)
    OR ([State] IN (N'Active', N'Superseded') AND [ActivatedByRequestId] IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsVersion] ADD CONSTRAINT [CK_ApplicationSettingsVersion_ContentHash_Hex] CHECK (LEN([ContentHash]) = 64 AND [ContentHash] NOT LIKE '%[^0-9a-f]%');");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsVersion] ADD CONSTRAINT [CK_ApplicationSettingsVersion_VersionNumber] CHECK ([VersionNumber] >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_ApplicationSettingsVersion_OneDraft] ON [catalog].[ApplicationSettingsVersion] ([ApplicationId]) WHERE [State] = N'Draft';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_ApplicationSettingsVersion_OneActive] ON [catalog].[ApplicationSettingsVersion] ([ApplicationId]) WHERE [State] = N'Active';");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsRole] ADD CONSTRAINT [CK_ApplicationSettingsRole_Requirement] CHECK ([Requirement] IN (N'Optional', N'Required'));");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsRole] ADD CONSTRAINT [CK_ApplicationSettingsRole_Holder] CHECK (
    ([HolderKind] IS NULL AND [HolderId] IS NULL)
    OR ([HolderKind] IN (N'Person', N'Group') AND [HolderId] IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsRequest] ADD CONSTRAINT [CK_ApplicationSettingsRequest_State] CHECK (
    ([State] = N'Proposed' AND [Checker] IS NULL AND [DecisionReason] IS NULL AND [DecidedAtUtc] IS NULL)
    OR ([State] = N'Withdrawn' AND [Checker] IS NOT NULL AND [Checker] = [Maker] AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] = N'Rejected' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [Checker] <> [ProposedOwner] AND [DecisionReason] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] = N'Approved' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [Checker] <> [ProposedOwner] AND [DecidedAtUtc] IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsRequest] ADD CONSTRAINT [CK_ApplicationSettingsRequest_ContentHash_Hex] CHECK (LEN([ContentHash]) = 64 AND [ContentHash] NOT LIKE '%[^0-9a-f]%');");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsRequest] ADD CONSTRAINT [CK_ApplicationSettingsRequest_VersionNumber] CHECK ([VersionNumber] >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_ApplicationSettingsRequest_OnePending] ON [catalog].[ApplicationSettingsRequest] ([ApplicationId]) WHERE [State] = N'Proposed';");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsRequest] ADD CONSTRAINT [AK_ApplicationSettingsRequest_Id_VersionId] UNIQUE ([Id], [SettingsVersionId]);");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsVersion] ADD CONSTRAINT [FK_ApplicationSettingsVersion_Request_Names] FOREIGN KEY ([ActivatedByRequestId], [Id]) REFERENCES [catalog].[ApplicationSettingsRequest] ([Id], [SettingsVersionId]);");
            migrationBuilder.Sql(@"CREATE TRIGGER [catalog].[TR_Application_KeyImmutable] ON [catalog].[Application]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51101, 'catalog.Application rows cannot be deleted (DL-066).', 1;
    IF UPDATE([Id]) OR UPDATE([Key])
        THROW 51102, 'Application Id and Key are immutable (DL-066).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [catalog].[TR_ApplicationRoleType_Guarded] ON [catalog].[ApplicationRoleType]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51103, 'catalog.ApplicationRoleType rows cannot be deleted (DL-066).', 1;
    IF NOT EXISTS (SELECT 1 FROM deleted) AND EXISTS (SELECT 1 FROM inserted WHERE [State] <> N'Proposed')
        THROW 51104, 'A new application role type must be proposed and approved by a second person (DL-066).', 1;
    IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([Code]) OR UPDATE([DisplayName]) OR UPDATE([Maker]) OR UPDATE([Reason]) OR UPDATE([ProposedAtUtc]))
        THROW 51105, 'Application role type proposal fields are immutable (DL-066).', 1;
    IF EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Proposed')
        THROW 51106, 'Built-in and decided application role types are immutable (DL-066).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [catalog].[TR_ApplicationSettingsVersion_Immutable] ON [catalog].[ApplicationSettingsVersion]
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
            migrationBuilder.Sql(@"CREATE TRIGGER [catalog].[TR_ApplicationSettingsRole_DraftOnly] ON [catalog].[ApplicationSettingsRole]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM (SELECT [SettingsVersionId] FROM inserted UNION SELECT [SettingsVersionId] FROM deleted) c
        JOIN [catalog].[ApplicationSettingsVersion] v WITH (READCOMMITTEDLOCK) ON v.[Id] = c.[SettingsVersionId]
        WHERE v.[State] <> N'Draft')
        THROW 51110, 'Roles of an active or superseded application settings version are immutable (DL-066).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [catalog].[TR_ApplicationSettingsRequest_DecidedImmutable] ON [catalog].[ApplicationSettingsRequest]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51111, 'catalog.ApplicationSettingsRequest rows are immutable and cannot be deleted (DL-066).', 1;
    IF UPDATE([Id]) OR UPDATE([ApplicationId]) OR UPDATE([SettingsVersionId]) OR UPDATE([VersionNumber]) OR UPDATE([ContentHash]) OR UPDATE([ProposedOwner]) OR UPDATE([Maker]) OR UPDATE([Reason]) OR UPDATE([ProposedAtUtc])
        THROW 51112, 'catalog.ApplicationSettingsRequest proposal fields are immutable (DL-066).', 1;
    IF EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Proposed')
        THROW 51113, 'Decided catalog.ApplicationSettingsRequest rows are immutable (DL-066).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the catalog with its history; only valid on a database that holds no catalog data worth keeping (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [catalog].[TR_ApplicationSettingsRequest_DecidedImmutable];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [catalog].[TR_ApplicationSettingsRole_DraftOnly];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [catalog].[TR_ApplicationSettingsVersion_Immutable];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [catalog].[TR_ApplicationRoleType_Guarded];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [catalog].[TR_Application_KeyImmutable];");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[ApplicationSettingsVersion] DROP CONSTRAINT [FK_ApplicationSettingsVersion_Request_Names];");
            migrationBuilder.DropForeignKey(
                name: "FK_ApplicationSettingsRequest_ApplicationSettingsVersion_SettingsVersionId",
                schema: "catalog",
                table: "ApplicationSettingsRequest");

            migrationBuilder.DropTable(
                name: "ApplicationSettingsRole",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ApplicationRoleType",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ApplicationSettingsVersion",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ApplicationSettingsRequest",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Application",
                schema: "catalog");
        }
    }
}
