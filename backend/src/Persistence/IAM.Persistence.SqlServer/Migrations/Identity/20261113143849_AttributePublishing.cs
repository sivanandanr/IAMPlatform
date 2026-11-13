using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Identity
{
    /// <inheritdoc />
    public partial class AttributePublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PublishedByRequestId",
                schema: "identity",
                table: "AttributeDefinitionVersion",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AttributePublishRequest",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttributeDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttributeDefinitionVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_AttributePublishRequest", x => x.Id);
                    table.CheckConstraint("CK_AttributePublishRequest_Checker_Code", "DATALENGTH([Checker]) > 0 AND DATALENGTH([Checker]) = DATALENGTH(LTRIM(RTRIM([Checker])))");
                    table.CheckConstraint("CK_AttributePublishRequest_ContentHash_Code", "DATALENGTH([ContentHash]) > 0 AND DATALENGTH([ContentHash]) = DATALENGTH(LTRIM(RTRIM([ContentHash])))");
                    table.CheckConstraint("CK_AttributePublishRequest_Maker_Code", "DATALENGTH([Maker]) > 0 AND DATALENGTH([Maker]) = DATALENGTH(LTRIM(RTRIM([Maker])))");
                    table.CheckConstraint("CK_AttributePublishRequest_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.ForeignKey(
                        name: "FK_AttributePublishRequest_AttributeDefinitionVersion_AttributeDefinitionVersionId",
                        column: x => x.AttributeDefinitionVersionId,
                        principalSchema: "identity",
                        principalTable: "AttributeDefinitionVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttributePublishRequest_AttributeDefinition_AttributeDefinitionId",
                        column: x => x.AttributeDefinitionId,
                        principalSchema: "identity",
                        principalTable: "AttributeDefinition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttributeDefinitionVersion_PublishedByRequestId",
                schema: "identity",
                table: "AttributeDefinitionVersion",
                column: "PublishedByRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_AttributePublishRequest_AttributeDefinitionId",
                schema: "identity",
                table: "AttributePublishRequest",
                column: "AttributeDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_AttributePublishRequest_AttributeDefinitionVersionId",
                schema: "identity",
                table: "AttributePublishRequest",
                column: "AttributeDefinitionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_AttributePublishRequest_State_ProposedAtUtc",
                schema: "identity",
                table: "AttributePublishRequest",
                columns: new[] { "State", "ProposedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_AttributeDefinitionVersion_AttributePublishRequest_PublishedByRequestId",
                schema: "identity",
                table: "AttributeDefinitionVersion",
                column: "PublishedByRequestId",
                principalSchema: "identity",
                principalTable: "AttributePublishRequest",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            // DL-007 / DL-064, hand-written and reviewed (not generated). Lifts the S1-08 draft-only lock: a version can now
            // be published, but only by an approved publish request that names it, and a published version can only be
            // retired with its content unchanged. Trigger names match the HasTrigger declarations in IdentityDbContext.
            migrationBuilder.Sql("ALTER TABLE [identity].[AttributeDefinitionVersion] DROP CONSTRAINT [CK_AttributeDefinitionVersion_State_DraftOnly];");
            migrationBuilder.Sql(@"ALTER TABLE [identity].[AttributeDefinitionVersion] ADD CONSTRAINT [CK_AttributeDefinitionVersion_State_Published] CHECK (
    ([State] = N'Draft' AND [PublishedByRequestId] IS NULL)
    OR ([State] IN (N'Published', N'Retired') AND [PublishedByRequestId] IS NOT NULL));");

            // Requests: known states, the shared second-person shape (no self-decision), a hex hash, and one pending request
            // per attribute.
            migrationBuilder.Sql(@"ALTER TABLE [identity].[AttributePublishRequest] ADD CONSTRAINT [CK_AttributePublishRequest_State] CHECK (
    ([State] = N'Proposed' AND [Checker] IS NULL AND [DecisionReason] IS NULL AND [DecidedAtUtc] IS NULL)
    OR ([State] = N'Withdrawn' AND [Checker] IS NOT NULL AND [Checker] = [Maker] AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] = N'Rejected' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecisionReason] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] = N'Approved' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecidedAtUtc] IS NOT NULL));");
            migrationBuilder.Sql("ALTER TABLE [identity].[AttributePublishRequest] ADD CONSTRAINT [CK_AttributePublishRequest_ContentHash_Hex] CHECK (LEN([ContentHash]) = 64 AND [ContentHash] NOT LIKE '%[^0-9a-f]%');");
            migrationBuilder.Sql("ALTER TABLE [identity].[AttributePublishRequest] ADD CONSTRAINT [CK_AttributePublishRequest_VersionNumber] CHECK ([VersionNumber] >= 1);");
            migrationBuilder.Sql("CREATE UNIQUE INDEX [UX_AttributePublishRequest_OnePending] ON [identity].[AttributePublishRequest] ([AttributeDefinitionId]) WHERE [State] = N'Proposed';");

            // A published version names a request that names it back.
            migrationBuilder.Sql("ALTER TABLE [identity].[AttributePublishRequest] ADD CONSTRAINT [AK_AttributePublishRequest_Id_VersionId] UNIQUE ([Id], [AttributeDefinitionVersionId]);");
            migrationBuilder.Sql("ALTER TABLE [identity].[AttributeDefinitionVersion] ADD CONSTRAINT [FK_AttributeDefinitionVersion_PublishRequest_Names] FOREIGN KEY ([PublishedByRequestId], [Id]) REFERENCES [identity].[AttributePublishRequest] ([Id], [AttributeDefinitionVersionId]);");

            // Versions: identity fields never change; a non-draft version never changes or disappears, except retiring a
            // published one with its content unchanged; a draft becomes published only by an approved request naming it.
            // Display names are compared in binary, so even a change of case is refused. READCOMMITTEDLOCK reads the
            // committed request even under read-committed snapshot.
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

            // Requests: what was proposed, by whom and why never changes; a decided request never changes; none is removed.
            migrationBuilder.Sql(@"CREATE TRIGGER [identity].[TR_AttributePublishRequest_DecidedImmutable] ON [identity].[AttributePublishRequest]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51015, 'identity.AttributePublishRequest rows are immutable and cannot be deleted (DL-064).', 1;
    IF UPDATE([Id]) OR UPDATE([AttributeDefinitionId]) OR UPDATE([AttributeDefinitionVersionId]) OR UPDATE([VersionNumber]) OR UPDATE([ContentHash]) OR UPDATE([Maker]) OR UPDATE([Reason]) OR UPDATE([ProposedAtUtc])
        THROW 51016, 'identity.AttributePublishRequest proposal fields are immutable (DL-064).', 1;
    IF EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Proposed')
        THROW 51017, 'Decided identity.AttributePublishRequest rows are immutable (DL-064).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only valid while nothing has been published.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [identity].[TR_AttributePublishRequest_DecidedImmutable];");
            migrationBuilder.Sql("ALTER TABLE [identity].[AttributeDefinitionVersion] DROP CONSTRAINT [FK_AttributeDefinitionVersion_PublishRequest_Names];");
            migrationBuilder.Sql("ALTER TABLE [identity].[AttributeDefinitionVersion] DROP CONSTRAINT [CK_AttributeDefinitionVersion_State_Published];");
            migrationBuilder.Sql("ALTER TABLE [identity].[AttributeDefinitionVersion] ADD CONSTRAINT [CK_AttributeDefinitionVersion_State_DraftOnly] CHECK ([State] = N'Draft');");
            migrationBuilder.Sql(@"ALTER TRIGGER [identity].[TR_AttributeDefinitionVersion_Immutable] ON [identity].[AttributeDefinitionVersion]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND (UPDATE([Id]) OR UPDATE([AttributeDefinitionId]) OR UPDATE([VersionNumber]))
        THROW 51013, 'Attribute definition version Id, AttributeDefinitionId and VersionNumber are immutable (IAM-WF-019).', 1;
    IF EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51011, 'Published or retired attribute definition versions are immutable (IAM-WF-019).', 1;
END;");

            migrationBuilder.DropForeignKey(
                name: "FK_AttributeDefinitionVersion_AttributePublishRequest_PublishedByRequestId",
                schema: "identity",
                table: "AttributeDefinitionVersion");

            migrationBuilder.DropTable(
                name: "AttributePublishRequest",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "IX_AttributeDefinitionVersion_PublishedByRequestId",
                schema: "identity",
                table: "AttributeDefinitionVersion");

            migrationBuilder.DropColumn(
                name: "PublishedByRequestId",
                schema: "identity",
                table: "AttributeDefinitionVersion");
        }
    }
}
