using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Organization
{
    /// <inheritdoc />
    public partial class Workgroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "organization");

            migrationBuilder.CreateTable(
                name: "Workgroup",
                schema: "organization",
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
                    table.PrimaryKey("PK_Workgroup", x => x.Id);
                    table.CheckConstraint("CK_Workgroup_Key_Code", "DATALENGTH([Key]) > 0 AND DATALENGTH([Key]) = DATALENGTH(LTRIM(RTRIM([Key])))");
                });

            migrationBuilder.CreateTable(
                name: "WorkgroupChangeRequest",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkgroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_WorkgroupChangeRequest", x => x.Id);
                    table.CheckConstraint("CK_WorkgroupChangeRequest_Checker_Code", "DATALENGTH([Checker]) > 0 AND DATALENGTH([Checker]) = DATALENGTH(LTRIM(RTRIM([Checker])))");
                    table.CheckConstraint("CK_WorkgroupChangeRequest_ContentHash_Code", "DATALENGTH([ContentHash]) > 0 AND DATALENGTH([ContentHash]) = DATALENGTH(LTRIM(RTRIM([ContentHash])))");
                    table.CheckConstraint("CK_WorkgroupChangeRequest_Maker_Code", "DATALENGTH([Maker]) > 0 AND DATALENGTH([Maker]) = DATALENGTH(LTRIM(RTRIM([Maker])))");
                    table.CheckConstraint("CK_WorkgroupChangeRequest_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.ForeignKey(
                        name: "FK_WorkgroupChangeRequest_Workgroup_WorkgroupId",
                        column: x => x.WorkgroupId,
                        principalSchema: "organization",
                        principalTable: "Workgroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkgroupVersion",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkgroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
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
                    table.PrimaryKey("PK_WorkgroupVersion", x => x.Id);
                    table.CheckConstraint("CK_WorkgroupVersion_ContentHash_Code", "DATALENGTH([ContentHash]) > 0 AND DATALENGTH([ContentHash]) = DATALENGTH(LTRIM(RTRIM([ContentHash])))");
                    table.CheckConstraint("CK_WorkgroupVersion_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.ForeignKey(
                        name: "FK_WorkgroupVersion_WorkgroupChangeRequest_ActivatedByRequestId",
                        column: x => x.ActivatedByRequestId,
                        principalSchema: "organization",
                        principalTable: "WorkgroupChangeRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkgroupVersion_Workgroup_WorkgroupId",
                        column: x => x.WorkgroupId,
                        principalSchema: "organization",
                        principalTable: "Workgroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkgroupMember",
                schema: "organization",
                columns: table => new
                {
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Person = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkgroupMember", x => new { x.VersionId, x.Person });
                    table.CheckConstraint("CK_WorkgroupMember_Person_Code", "DATALENGTH([Person]) > 0 AND DATALENGTH([Person]) = DATALENGTH(LTRIM(RTRIM([Person])))");
                    table.ForeignKey(
                        name: "FK_WorkgroupMember_WorkgroupVersion_VersionId",
                        column: x => x.VersionId,
                        principalSchema: "organization",
                        principalTable: "WorkgroupVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workgroup_Key",
                schema: "organization",
                table: "Workgroup",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkgroupChangeRequest_State_ProposedAtUtc",
                schema: "organization",
                table: "WorkgroupChangeRequest",
                columns: new[] { "State", "ProposedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkgroupChangeRequest_VersionId",
                schema: "organization",
                table: "WorkgroupChangeRequest",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkgroupChangeRequest_WorkgroupId",
                schema: "organization",
                table: "WorkgroupChangeRequest",
                column: "WorkgroupId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkgroupVersion_ActivatedByRequestId",
                schema: "organization",
                table: "WorkgroupVersion",
                column: "ActivatedByRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkgroupVersion_WorkgroupId_VersionNumber",
                schema: "organization",
                table: "WorkgroupVersion",
                columns: new[] { "WorkgroupId", "VersionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkgroupChangeRequest_WorkgroupVersion_VersionId",
                schema: "organization",
                table: "WorkgroupChangeRequest",
                column: "VersionId",
                principalSchema: "organization",
                principalTable: "WorkgroupVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            // DL-068, hand-written and reviewed (not generated). The built-in fallback workgroup (iam_managers) is seeded with an
            // empty draft before the guards exist. Workgroups are never deleted; a member list becomes active only by an
            // approved request naming it, decided by someone who is not a member of it; active and superseded lists and
            // their members never change, except an active list being superseded. Trigger names match OrganizationDbContext.
            migrationBuilder.Sql(@"INSERT INTO [organization].[Workgroup] ([Id], [Key], [DisplayName], [CreatedAtUtc], [CreatedBy], [ModifiedAtUtc], [ModifiedBy], [Version]) VALUES ('0193a1f0-0000-7000-8000-0000000000f1', N'iam_managers', N'IAM managers', '2026-11-14T00:00:00+00:00', N'migration:Workgroups', '2026-11-14T00:00:00+00:00', N'migration:Workgroups', 1);");
            migrationBuilder.Sql(@"INSERT INTO [organization].[WorkgroupVersion] ([Id], [WorkgroupId], [VersionNumber], [State], [ContentHash], [ActivatedByRequestId], [CreatedAtUtc], [CreatedBy], [ModifiedAtUtc], [ModifiedBy], [Version]) VALUES ('0193a1f0-0000-7000-8000-0000000000f2', '0193a1f0-0000-7000-8000-0000000000f1', 1, N'Draft', N'66aca7e3a7e5de11db95a6b17e4e8c47bddcc5cbabda2e57fa3f61f82f5ea2a4', NULL, '2026-11-14T00:00:00+00:00', N'migration:Workgroups', '2026-11-14T00:00:00+00:00', N'migration:Workgroups', 1);");
            migrationBuilder.Sql(@"ALTER TABLE [organization].[Workgroup] ADD CONSTRAINT [CK_Workgroup_Key_Format] CHECK (LEN([Key]) BETWEEN 2 AND 64 AND DATALENGTH([Key]) = 2 * LEN([Key]) AND [Key] LIKE '[a-z]%' AND [Key] NOT LIKE '%[^a-z0-9_]%');");
            migrationBuilder.Sql(@"ALTER TABLE [organization].[WorkgroupVersion] ADD CONSTRAINT [CK_WorkgroupVersion_State] CHECK (
    ([State] = N'Draft' AND [ActivatedByRequestId] IS NULL)
    OR ([State] IN (N'Active', N'Superseded') AND [ActivatedByRequestId] IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE [organization].[WorkgroupVersion] ADD CONSTRAINT [CK_WorkgroupVersion_ContentHash_Hex] CHECK (LEN([ContentHash]) = 64 AND [ContentHash] NOT LIKE '%[^0-9a-f]%');");
            migrationBuilder.Sql(@"ALTER TABLE [organization].[WorkgroupVersion] ADD CONSTRAINT [CK_WorkgroupVersion_VersionNumber] CHECK ([VersionNumber] >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_WorkgroupVersion_OneDraft] ON [organization].[WorkgroupVersion] ([WorkgroupId]) WHERE [State] = N'Draft';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_WorkgroupVersion_OneActive] ON [organization].[WorkgroupVersion] ([WorkgroupId]) WHERE [State] = N'Active';");
            migrationBuilder.Sql(@"ALTER TABLE [organization].[WorkgroupChangeRequest] ADD CONSTRAINT [CK_WorkgroupChangeRequest_State] CHECK (
    ([State] = N'Proposed' AND [Checker] IS NULL AND [DecisionReason] IS NULL AND [DecidedAtUtc] IS NULL)
    OR ([State] = N'Withdrawn' AND [Checker] IS NOT NULL AND [Checker] = [Maker] AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] = N'Rejected' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecisionReason] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] = N'Approved' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecidedAtUtc] IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE [organization].[WorkgroupChangeRequest] ADD CONSTRAINT [CK_WorkgroupChangeRequest_ContentHash_Hex] CHECK (LEN([ContentHash]) = 64 AND [ContentHash] NOT LIKE '%[^0-9a-f]%');");
            migrationBuilder.Sql(@"ALTER TABLE [organization].[WorkgroupChangeRequest] ADD CONSTRAINT [CK_WorkgroupChangeRequest_VersionNumber] CHECK ([VersionNumber] >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_WorkgroupChangeRequest_OnePending] ON [organization].[WorkgroupChangeRequest] ([WorkgroupId]) WHERE [State] = N'Proposed';");
            migrationBuilder.Sql(@"ALTER TABLE [organization].[WorkgroupChangeRequest] ADD CONSTRAINT [AK_WorkgroupChangeRequest_Id_VersionId] UNIQUE ([Id], [VersionId]);");
            migrationBuilder.Sql(@"ALTER TABLE [organization].[WorkgroupVersion] ADD CONSTRAINT [FK_WorkgroupVersion_Request_Names] FOREIGN KEY ([ActivatedByRequestId], [Id]) REFERENCES [organization].[WorkgroupChangeRequest] ([Id], [VersionId]);");
            migrationBuilder.Sql(@"CREATE TRIGGER [organization].[TR_Workgroup_KeyImmutable] ON [organization].[Workgroup]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51201, 'organization.Workgroup rows cannot be deleted (DL-068).', 1;
    IF UPDATE([Id]) OR UPDATE([Key])
        THROW 51202, 'Workgroup Id and Key are immutable (DL-068).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [organization].[TR_WorkgroupVersion_Immutable] ON [organization].[WorkgroupVersion]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([WorkgroupId]) OR UPDATE([VersionNumber]))
        THROW 51203, 'Workgroup version Id, WorkgroupId and VersionNumber are immutable (DL-068).', 1;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51204, 'Active or superseded workgroup member lists are immutable (DL-068).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE d.[State] <> N'Draft' AND NOT (
            d.[State] = N'Active' AND i.[State] = N'Superseded'
            AND i.[ContentHash] = d.[ContentHash] AND i.[ActivatedByRequestId] = d.[ActivatedByRequestId]))
        THROW 51204, 'Active or superseded workgroup member lists are immutable; an active list can only be superseded (DL-068).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        WHERE i.[State] <> N'Draft' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND (i.[State] <> N'Active' OR NOT EXISTS (
            SELECT 1 FROM [organization].[WorkgroupChangeRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[ActivatedByRequestId] AND r.[VersionId] = i.[Id] AND r.[State] = N'Approved'
            AND r.[WorkgroupId] = i.[WorkgroupId] AND r.[VersionNumber] = i.[VersionNumber] AND r.[ContentHash] = i.[ContentHash])))
        THROW 51205, 'Workgroup member lists become active only by an approved request naming them (DL-068).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        JOIN [organization].[WorkgroupChangeRequest] r WITH (READCOMMITTEDLOCK) ON r.[Id] = i.[ActivatedByRequestId]
        JOIN [organization].[WorkgroupMember] m WITH (READCOMMITTEDLOCK) ON m.[VersionId] = i.[Id]
        WHERE i.[State] = N'Active' AND (d.[Id] IS NULL OR d.[State] = N'Draft') AND m.[Person] = r.[Checker])
        THROW 51206, 'A member of a workgroup member list cannot approve it (DL-068).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [organization].[TR_WorkgroupMember_DraftOnly] ON [organization].[WorkgroupMember]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM (SELECT [VersionId] FROM inserted UNION SELECT [VersionId] FROM deleted) c
        JOIN [organization].[WorkgroupVersion] v WITH (READCOMMITTEDLOCK) ON v.[Id] = c.[VersionId]
        WHERE v.[State] <> N'Draft')
        THROW 51207, 'Members of an active or superseded workgroup member list are immutable (DL-068).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [organization].[TR_WorkgroupChangeRequest_DecidedImmutable] ON [organization].[WorkgroupChangeRequest]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51208, 'organization.WorkgroupChangeRequest rows are immutable and cannot be deleted (DL-068).', 1;
    IF UPDATE([Id]) OR UPDATE([WorkgroupId]) OR UPDATE([VersionId]) OR UPDATE([VersionNumber]) OR UPDATE([ContentHash]) OR UPDATE([Maker]) OR UPDATE([Reason]) OR UPDATE([ProposedAtUtc])
        THROW 51209, 'organization.WorkgroupChangeRequest proposal fields are immutable (DL-068).', 1;
    IF EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Proposed')
        THROW 51210, 'Decided organization.WorkgroupChangeRequest rows are immutable (DL-068).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the workgroups with their history; only valid where no workgroup data matters (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [organization].[TR_WorkgroupChangeRequest_DecidedImmutable];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [organization].[TR_WorkgroupMember_DraftOnly];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [organization].[TR_WorkgroupVersion_Immutable];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [organization].[TR_Workgroup_KeyImmutable];");
            migrationBuilder.Sql(@"ALTER TABLE [organization].[WorkgroupVersion] DROP CONSTRAINT [FK_WorkgroupVersion_Request_Names];");
            migrationBuilder.DropForeignKey(
                name: "FK_WorkgroupChangeRequest_WorkgroupVersion_VersionId",
                schema: "organization",
                table: "WorkgroupChangeRequest");

            migrationBuilder.DropTable(
                name: "WorkgroupMember",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "WorkgroupVersion",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "WorkgroupChangeRequest",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "Workgroup",
                schema: "organization");
        }
    }
}
