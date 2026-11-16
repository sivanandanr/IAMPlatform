using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class Entitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Entitlement",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_Entitlement", x => x.Id);
                    table.CheckConstraint("CK_Entitlement_Key_Code", "DATALENGTH([Key]) > 0 AND DATALENGTH([Key]) = DATALENGTH(LTRIM(RTRIM([Key])))");
                    table.ForeignKey(
                        name: "FK_Entitlement_Application_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "catalog",
                        principalTable: "Application",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EntitlementChangeRequest",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntitlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OwnerKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OwnerId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AcceptedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2"),
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
                    table.PrimaryKey("PK_EntitlementChangeRequest", x => x.Id);
                    table.CheckConstraint("CK_EntitlementChangeRequest_AcceptedBy_Code", "DATALENGTH([AcceptedBy]) > 0 AND DATALENGTH([AcceptedBy]) = DATALENGTH(LTRIM(RTRIM([AcceptedBy])))");
                    table.CheckConstraint("CK_EntitlementChangeRequest_Checker_Code", "DATALENGTH([Checker]) > 0 AND DATALENGTH([Checker]) = DATALENGTH(LTRIM(RTRIM([Checker])))");
                    table.CheckConstraint("CK_EntitlementChangeRequest_ContentHash_Code", "DATALENGTH([ContentHash]) > 0 AND DATALENGTH([ContentHash]) = DATALENGTH(LTRIM(RTRIM([ContentHash])))");
                    table.CheckConstraint("CK_EntitlementChangeRequest_Maker_Code", "DATALENGTH([Maker]) > 0 AND DATALENGTH([Maker]) = DATALENGTH(LTRIM(RTRIM([Maker])))");
                    table.CheckConstraint("CK_EntitlementChangeRequest_OwnerId_Code", "DATALENGTH([OwnerId]) > 0 AND DATALENGTH([OwnerId]) = DATALENGTH(LTRIM(RTRIM([OwnerId])))");
                    table.CheckConstraint("CK_EntitlementChangeRequest_OwnerKind_Code", "DATALENGTH([OwnerKind]) > 0 AND DATALENGTH([OwnerKind]) = DATALENGTH(LTRIM(RTRIM([OwnerKind])))");
                    table.CheckConstraint("CK_EntitlementChangeRequest_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.ForeignKey(
                        name: "FK_EntitlementChangeRequest_Entitlement_EntitlementId",
                        column: x => x.EntitlementId,
                        principalSchema: "catalog",
                        principalTable: "Entitlement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EntitlementVersion",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntitlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TechnicalName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    OwnerKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OwnerId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    EscalationWorkgroup = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Privileged = table.Column<bool>(type: "bit", nullable: false),
                    Risk = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AllowedAccountClasses = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AccessForMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AccessForDays = table.Column<int>(type: "int", nullable: true),
                    AccessForDefaultDays = table.Column<int>(type: "int", nullable: true),
                    AccessForMaxDays = table.Column<int>(type: "int", nullable: true),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    EditedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OwnerAcceptedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2"),
                    OwnerAcceptedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
                    ActivatedByRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntitlementVersion", x => x.Id);
                    table.CheckConstraint("CK_EntitlementVersion_AccessForMode_Code", "DATALENGTH([AccessForMode]) > 0 AND DATALENGTH([AccessForMode]) = DATALENGTH(LTRIM(RTRIM([AccessForMode])))");
                    table.CheckConstraint("CK_EntitlementVersion_AllowedAccountClasses_Code", "DATALENGTH([AllowedAccountClasses]) > 0 AND DATALENGTH([AllowedAccountClasses]) = DATALENGTH(LTRIM(RTRIM([AllowedAccountClasses])))");
                    table.CheckConstraint("CK_EntitlementVersion_ContentHash_Code", "DATALENGTH([ContentHash]) > 0 AND DATALENGTH([ContentHash]) = DATALENGTH(LTRIM(RTRIM([ContentHash])))");
                    table.CheckConstraint("CK_EntitlementVersion_EditedBy_Code", "DATALENGTH([EditedBy]) > 0 AND DATALENGTH([EditedBy]) = DATALENGTH(LTRIM(RTRIM([EditedBy])))");
                    table.CheckConstraint("CK_EntitlementVersion_EscalationWorkgroup_Code", "DATALENGTH([EscalationWorkgroup]) > 0 AND DATALENGTH([EscalationWorkgroup]) = DATALENGTH(LTRIM(RTRIM([EscalationWorkgroup])))");
                    table.CheckConstraint("CK_EntitlementVersion_OwnerAcceptedBy_Code", "DATALENGTH([OwnerAcceptedBy]) > 0 AND DATALENGTH([OwnerAcceptedBy]) = DATALENGTH(LTRIM(RTRIM([OwnerAcceptedBy])))");
                    table.CheckConstraint("CK_EntitlementVersion_OwnerId_Code", "DATALENGTH([OwnerId]) > 0 AND DATALENGTH([OwnerId]) = DATALENGTH(LTRIM(RTRIM([OwnerId])))");
                    table.CheckConstraint("CK_EntitlementVersion_OwnerKind_Code", "DATALENGTH([OwnerKind]) > 0 AND DATALENGTH([OwnerKind]) = DATALENGTH(LTRIM(RTRIM([OwnerKind])))");
                    table.CheckConstraint("CK_EntitlementVersion_Risk_Code", "DATALENGTH([Risk]) > 0 AND DATALENGTH([Risk]) = DATALENGTH(LTRIM(RTRIM([Risk])))");
                    table.CheckConstraint("CK_EntitlementVersion_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.CheckConstraint("CK_EntitlementVersion_Type_Code", "DATALENGTH([Type]) > 0 AND DATALENGTH([Type]) = DATALENGTH(LTRIM(RTRIM([Type])))");
                    table.ForeignKey(
                        name: "FK_EntitlementVersion_EntitlementChangeRequest_ActivatedByRequestId",
                        column: x => x.ActivatedByRequestId,
                        principalSchema: "catalog",
                        principalTable: "EntitlementChangeRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EntitlementVersion_Entitlement_EntitlementId",
                        column: x => x.EntitlementId,
                        principalSchema: "catalog",
                        principalTable: "Entitlement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Entitlement_ApplicationId_Key",
                schema: "catalog",
                table: "Entitlement",
                columns: new[] { "ApplicationId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EntitlementChangeRequest_EntitlementId",
                schema: "catalog",
                table: "EntitlementChangeRequest",
                column: "EntitlementId");

            migrationBuilder.CreateIndex(
                name: "IX_EntitlementChangeRequest_State_ProposedAtUtc",
                schema: "catalog",
                table: "EntitlementChangeRequest",
                columns: new[] { "State", "ProposedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EntitlementChangeRequest_VersionId",
                schema: "catalog",
                table: "EntitlementChangeRequest",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_EntitlementVersion_ActivatedByRequestId",
                schema: "catalog",
                table: "EntitlementVersion",
                column: "ActivatedByRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_EntitlementVersion_EntitlementId_VersionNumber",
                schema: "catalog",
                table: "EntitlementVersion",
                columns: new[] { "EntitlementId", "VersionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_EntitlementChangeRequest_EntitlementVersion_VersionId",
                schema: "catalog",
                table: "EntitlementChangeRequest",
                column: "VersionId",
                principalSchema: "catalog",
                principalTable: "EntitlementVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            // DL-087, hand-written and reviewed (not generated). Entitlements are never deleted and keep their id, application
            // and key. A version becomes active only by an approved request naming its exact content and owner, decided by
            // someone other than the person who wrote the content or accepted ownership; active and superseded versions never
            // change, except an active version being superseded. Privileged access never sits on a Primary account and always
            // has an end date; a Secondary account is only for privileged access (spec v3.2 §2.5, §3.1, §6.4).
            // Trigger names match the HasTrigger declarations in CatalogDbContext.
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[Entitlement] ADD CONSTRAINT [CK_Entitlement_Key_Format] CHECK (LEN([Key]) BETWEEN 2 AND 64 AND DATALENGTH([Key]) = 2 * LEN([Key]) AND [Key] LIKE '[a-z]%' AND [Key] NOT LIKE '%[^a-z0-9_]%');");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementVersion] ADD CONSTRAINT [CK_EntitlementVersion_State] CHECK (
    ([State] = N'Draft' AND [ActivatedByRequestId] IS NULL)
    OR ([State] IN (N'Active', N'Superseded') AND [ActivatedByRequestId] IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementVersion] ADD CONSTRAINT [CK_EntitlementVersion_ContentHash_Hex] CHECK (LEN([ContentHash]) = 64 AND [ContentHash] NOT LIKE '%[^0-9a-f]%');");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementVersion] ADD CONSTRAINT [CK_EntitlementVersion_VersionNumber] CHECK ([VersionNumber] >= 1);");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementVersion] ADD CONSTRAINT [CK_EntitlementVersion_Codes] CHECK ([Type] IN (N'SecurityGroup', N'DistributionList', N'SharedMailbox') AND [Risk] IN (N'Low', N'Medium', N'High', N'Critical') AND [OwnerKind] IN (N'Person', N'Group'));");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementVersion] ADD CONSTRAINT [CK_EntitlementVersion_AccountClasses] CHECK ([AllowedAccountClasses] IN (N'Primary', N'Secondary', N'Primary,Secondary', N'Service', N'Primary,Service', N'Secondary,Service', N'Primary,Secondary,Service', N'Shared', N'Primary,Shared', N'Secondary,Shared', N'Primary,Secondary,Shared', N'Service,Shared', N'Primary,Service,Shared', N'Secondary,Service,Shared', N'Primary,Secondary,Service,Shared') AND (([Privileged] = 1 AND [AllowedAccountClasses] NOT LIKE N'%Primary%') OR ([Privileged] = 0 AND [AllowedAccountClasses] NOT LIKE N'%Secondary%')));");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementVersion] ADD CONSTRAINT [CK_EntitlementVersion_AccessFor] CHECK (
    ([AccessForMode] = N'NoExpiry' AND [Privileged] = 0 AND [AccessForDays] IS NULL AND [AccessForDefaultDays] IS NULL AND [AccessForMaxDays] IS NULL)
    OR ([AccessForMode] = N'FixedDays' AND [AccessForDays] BETWEEN 1 AND 3650 AND [AccessForDefaultDays] IS NULL AND [AccessForMaxDays] IS NULL)
    OR ([AccessForMode] = N'RequesterChooses' AND [AccessForDays] IS NULL AND [AccessForDefaultDays] BETWEEN 1 AND 3650 AND [AccessForMaxDays] BETWEEN 1 AND 3650 AND [AccessForDefaultDays] <= [AccessForMaxDays]));");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementVersion] ADD CONSTRAINT [CK_EntitlementVersion_Acceptance] CHECK (([OwnerAcceptedBy] IS NULL AND [OwnerAcceptedAtUtc] IS NULL) OR ([OwnerAcceptedBy] IS NOT NULL AND [OwnerAcceptedAtUtc] IS NOT NULL));");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_EntitlementVersion_OneDraft] ON [catalog].[EntitlementVersion] ([EntitlementId]) WHERE [State] = N'Draft';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_EntitlementVersion_OneActive] ON [catalog].[EntitlementVersion] ([EntitlementId]) WHERE [State] = N'Active';");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementChangeRequest] ADD CONSTRAINT [CK_EntitlementChangeRequest_State] CHECK (
    ([State] = N'Proposed' AND [Checker] IS NULL AND [DecisionReason] IS NULL AND [DecidedAtUtc] IS NULL)
    OR ([State] = N'Withdrawn' AND [Checker] IS NOT NULL AND [Checker] = [Maker] AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] IN (N'Rejected', N'Approved') AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecidedAtUtc] IS NOT NULL
        AND ([AcceptedBy] IS NULL OR [Checker] <> [AcceptedBy])
        AND ([OwnerKind] <> N'Person' OR [Checker] <> [OwnerId])
        AND ([State] = N'Approved' OR [DecisionReason] IS NOT NULL)));");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementChangeRequest] ADD CONSTRAINT [CK_EntitlementChangeRequest_ContentHash_Hex] CHECK (LEN([ContentHash]) = 64 AND [ContentHash] NOT LIKE '%[^0-9a-f]%');");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementChangeRequest] ADD CONSTRAINT [CK_EntitlementChangeRequest_VersionNumber] CHECK ([VersionNumber] >= 1);");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementChangeRequest] ADD CONSTRAINT [CK_EntitlementChangeRequest_OwnerKind] CHECK ([OwnerKind] IN (N'Person', N'Group'));");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_EntitlementChangeRequest_OnePending] ON [catalog].[EntitlementChangeRequest] ([EntitlementId]) WHERE [State] = N'Proposed';");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementChangeRequest] ADD CONSTRAINT [AK_EntitlementChangeRequest_Id_VersionId] UNIQUE ([Id], [VersionId]);");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementVersion] ADD CONSTRAINT [FK_EntitlementVersion_Request_Names] FOREIGN KEY ([ActivatedByRequestId], [Id]) REFERENCES [catalog].[EntitlementChangeRequest] ([Id], [VersionId]);");
            migrationBuilder.Sql(@"CREATE TRIGGER [catalog].[TR_Entitlement_KeyImmutable] ON [catalog].[Entitlement]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51120, 'catalog.Entitlement rows cannot be deleted (DL-087).', 1;
    IF UPDATE([Id]) OR UPDATE([ApplicationId]) OR UPDATE([Key])
        THROW 51121, 'Entitlement Id, ApplicationId and Key are immutable (DL-087).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [catalog].[TR_EntitlementVersion_Immutable] ON [catalog].[EntitlementVersion]
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
            migrationBuilder.Sql(@"CREATE TRIGGER [catalog].[TR_EntitlementChangeRequest_DecidedImmutable] ON [catalog].[EntitlementChangeRequest]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51125, 'catalog.EntitlementChangeRequest rows are immutable and cannot be deleted (DL-087).', 1;
    IF UPDATE([Id]) OR UPDATE([EntitlementId]) OR UPDATE([VersionId]) OR UPDATE([VersionNumber]) OR UPDATE([ContentHash]) OR UPDATE([OwnerKind]) OR UPDATE([OwnerId]) OR UPDATE([AcceptedBy]) OR UPDATE([Maker]) OR UPDATE([Reason]) OR UPDATE([ProposedAtUtc])
        THROW 51126, 'catalog.EntitlementChangeRequest proposal fields are immutable (DL-087).', 1;
    IF EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Proposed')
        THROW 51127, 'Decided catalog.EntitlementChangeRequest rows are immutable (DL-087).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the entitlement catalog with its history; only valid on a database that holds no entitlement data worth keeping (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [catalog].[TR_EntitlementChangeRequest_DecidedImmutable];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [catalog].[TR_EntitlementVersion_Immutable];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [catalog].[TR_Entitlement_KeyImmutable];");
            migrationBuilder.Sql(@"ALTER TABLE [catalog].[EntitlementVersion] DROP CONSTRAINT [FK_EntitlementVersion_Request_Names];");
            migrationBuilder.DropForeignKey(
                name: "FK_EntitlementChangeRequest_EntitlementVersion_VersionId",
                schema: "catalog",
                table: "EntitlementChangeRequest");

            migrationBuilder.DropTable(
                name: "EntitlementVersion",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "EntitlementChangeRequest",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Entitlement",
                schema: "catalog");
        }
    }
}
