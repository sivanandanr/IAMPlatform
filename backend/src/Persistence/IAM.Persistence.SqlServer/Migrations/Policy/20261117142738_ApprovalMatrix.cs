using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Policy
{
    /// <inheritdoc />
    public partial class ApprovalMatrix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "policy");

            migrationBuilder.CreateTable(
                name: "ApprovalMatrix",
                schema: "policy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalMatrix", x => x.Id);
                    table.CheckConstraint("CK_ApprovalMatrix_Key_Code", "DATALENGTH([Key]) > 0 AND DATALENGTH([Key]) = DATALENGTH(LTRIM(RTRIM([Key])))");
                });

            migrationBuilder.CreateTable(
                name: "MatrixChangeRequest",
                schema: "policy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MatrixId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CompiledHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
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
                    table.PrimaryKey("PK_MatrixChangeRequest", x => x.Id);
                    table.CheckConstraint("CK_MatrixChangeRequest_Checker_Code", "DATALENGTH([Checker]) > 0 AND DATALENGTH([Checker]) = DATALENGTH(LTRIM(RTRIM([Checker])))");
                    table.CheckConstraint("CK_MatrixChangeRequest_CompiledHash_Code", "DATALENGTH([CompiledHash]) > 0 AND DATALENGTH([CompiledHash]) = DATALENGTH(LTRIM(RTRIM([CompiledHash])))");
                    table.CheckConstraint("CK_MatrixChangeRequest_ContentHash_Code", "DATALENGTH([ContentHash]) > 0 AND DATALENGTH([ContentHash]) = DATALENGTH(LTRIM(RTRIM([ContentHash])))");
                    table.CheckConstraint("CK_MatrixChangeRequest_Maker_Code", "DATALENGTH([Maker]) > 0 AND DATALENGTH([Maker]) = DATALENGTH(LTRIM(RTRIM([Maker])))");
                    table.CheckConstraint("CK_MatrixChangeRequest_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.ForeignKey(
                        name: "FK_MatrixChangeRequest_ApprovalMatrix_MatrixId",
                        column: x => x.MatrixId,
                        principalSchema: "policy",
                        principalTable: "ApprovalMatrix",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MatrixVersion",
                schema: "policy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MatrixId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Document = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CompiledHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
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
                    table.PrimaryKey("PK_MatrixVersion", x => x.Id);
                    table.CheckConstraint("CK_MatrixVersion_CompiledHash_Code", "DATALENGTH([CompiledHash]) > 0 AND DATALENGTH([CompiledHash]) = DATALENGTH(LTRIM(RTRIM([CompiledHash])))");
                    table.CheckConstraint("CK_MatrixVersion_ContentHash_Code", "DATALENGTH([ContentHash]) > 0 AND DATALENGTH([ContentHash]) = DATALENGTH(LTRIM(RTRIM([ContentHash])))");
                    table.CheckConstraint("CK_MatrixVersion_EditedBy_Code", "DATALENGTH([EditedBy]) > 0 AND DATALENGTH([EditedBy]) = DATALENGTH(LTRIM(RTRIM([EditedBy])))");
                    table.CheckConstraint("CK_MatrixVersion_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                    table.ForeignKey(
                        name: "FK_MatrixVersion_ApprovalMatrix_MatrixId",
                        column: x => x.MatrixId,
                        principalSchema: "policy",
                        principalTable: "ApprovalMatrix",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MatrixVersion_MatrixChangeRequest_ActivatedByRequestId",
                        column: x => x.ActivatedByRequestId,
                        principalSchema: "policy",
                        principalTable: "MatrixChangeRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalMatrix_Key",
                schema: "policy",
                table: "ApprovalMatrix",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MatrixChangeRequest_MatrixId",
                schema: "policy",
                table: "MatrixChangeRequest",
                column: "MatrixId");

            migrationBuilder.CreateIndex(
                name: "IX_MatrixChangeRequest_State_ProposedAtUtc",
                schema: "policy",
                table: "MatrixChangeRequest",
                columns: new[] { "State", "ProposedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MatrixChangeRequest_VersionId",
                schema: "policy",
                table: "MatrixChangeRequest",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_MatrixVersion_ActivatedByRequestId",
                schema: "policy",
                table: "MatrixVersion",
                column: "ActivatedByRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_MatrixVersion_MatrixId_VersionNumber",
                schema: "policy",
                table: "MatrixVersion",
                columns: new[] { "MatrixId", "VersionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MatrixChangeRequest_MatrixVersion_VersionId",
                schema: "policy",
                table: "MatrixChangeRequest",
                column: "VersionId",
                principalSchema: "policy",
                principalTable: "MatrixVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            // DL-090, hand-written and reviewed (not generated). One enterprise matrix, never deleted. A matrix version becomes
            // active only by an approved request naming its exact content and compiled hash, decided by someone other than the
            // maker and the content's author; active and superseded versions and decided requests never change, except an active
            // version being superseded. Trigger names match the HasTrigger declarations in PolicyDbContext.
            migrationBuilder.Sql(@"ALTER TABLE [policy].[ApprovalMatrix] ADD CONSTRAINT [CK_ApprovalMatrix_Key] CHECK ([Key] = N'enterprise');");
            migrationBuilder.Sql(@"ALTER TABLE [policy].[MatrixVersion] ADD CONSTRAINT [CK_MatrixVersion_State] CHECK (
    ([State] = N'Draft' AND [ActivatedByRequestId] IS NULL)
    OR ([State] IN (N'Active', N'Superseded') AND [ActivatedByRequestId] IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE [policy].[MatrixVersion] ADD CONSTRAINT [CK_MatrixVersion_Hashes] CHECK (LEN([ContentHash]) = 64 AND [ContentHash] NOT LIKE '%[^0-9a-f]%' AND LEN([CompiledHash]) = 64 AND [CompiledHash] NOT LIKE '%[^0-9a-f]%' AND [VersionNumber] >= 1 AND DATALENGTH([Document]) <= 8388608);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_MatrixVersion_OneDraft] ON [policy].[MatrixVersion] ([MatrixId]) WHERE [State] = N'Draft';");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_MatrixVersion_OneActive] ON [policy].[MatrixVersion] ([MatrixId]) WHERE [State] = N'Active';");
            migrationBuilder.Sql(@"ALTER TABLE [policy].[MatrixChangeRequest] ADD CONSTRAINT [CK_MatrixChangeRequest_State] CHECK (
    ([State] = N'Proposed' AND [Checker] IS NULL AND [DecisionReason] IS NULL AND [DecidedAtUtc] IS NULL)
    OR ([State] = N'Withdrawn' AND [Checker] IS NOT NULL AND [Checker] = [Maker] AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] = N'Rejected' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecisionReason] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL)
    OR ([State] = N'Approved' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecidedAtUtc] IS NOT NULL));");
            migrationBuilder.Sql(@"ALTER TABLE [policy].[MatrixChangeRequest] ADD CONSTRAINT [CK_MatrixChangeRequest_Hashes] CHECK (LEN([ContentHash]) = 64 AND [ContentHash] NOT LIKE '%[^0-9a-f]%' AND LEN([CompiledHash]) = 64 AND [CompiledHash] NOT LIKE '%[^0-9a-f]%' AND [VersionNumber] >= 1);");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_MatrixChangeRequest_OnePending] ON [policy].[MatrixChangeRequest] ([MatrixId]) WHERE [State] = N'Proposed';");
            migrationBuilder.Sql(@"ALTER TABLE [policy].[MatrixChangeRequest] ADD CONSTRAINT [AK_MatrixChangeRequest_Id_VersionId] UNIQUE ([Id], [VersionId]);");
            migrationBuilder.Sql(@"ALTER TABLE [policy].[MatrixVersion] ADD CONSTRAINT [FK_MatrixVersion_Request_Names] FOREIGN KEY ([ActivatedByRequestId], [Id]) REFERENCES [policy].[MatrixChangeRequest] ([Id], [VersionId]);");
            migrationBuilder.Sql(@"CREATE TRIGGER [policy].[TR_ApprovalMatrix_Immutable] ON [policy].[ApprovalMatrix]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51301, 'policy.ApprovalMatrix rows cannot be deleted (DL-090).', 1;
    IF EXISTS (SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id] WHERE i.[Key] <> d.[Key]) OR UPDATE([Id])
        THROW 51302, 'The approval matrix id and key are immutable (DL-090).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [policy].[TR_MatrixVersion_Immutable] ON [policy].[MatrixVersion]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) AND (UPDATE([Id]) OR UPDATE([MatrixId]) OR UPDATE([VersionNumber]))
        THROW 51303, 'Matrix version id, matrix and number are immutable (DL-090).', 1;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Draft')
        THROW 51304, 'Active or superseded matrix versions are immutable (DL-090).', 1;
    IF EXISTS (
        SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
        WHERE d.[State] <> N'Draft' AND NOT (
            d.[State] = N'Active' AND i.[State] = N'Superseded' AND i.[Document] = d.[Document] AND i.[ContentHash] = d.[ContentHash]
            AND i.[CompiledHash] = d.[CompiledHash] AND i.[EditedBy] = d.[EditedBy] AND i.[ActivatedByRequestId] = d.[ActivatedByRequestId]))
        THROW 51304, 'Active or superseded matrix versions are immutable; an active version can only be superseded (DL-090).', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.[Id] = i.[Id]
        WHERE i.[State] <> N'Draft' AND (d.[Id] IS NULL OR d.[State] = N'Draft')
        AND (i.[State] <> N'Active' OR NOT EXISTS (
            SELECT 1 FROM [policy].[MatrixChangeRequest] r WITH (READCOMMITTEDLOCK)
            WHERE r.[Id] = i.[ActivatedByRequestId] AND r.[VersionId] = i.[Id] AND r.[State] = N'Approved'
            AND r.[MatrixId] = i.[MatrixId] AND r.[VersionNumber] = i.[VersionNumber] AND r.[ContentHash] = i.[ContentHash]
            AND r.[CompiledHash] = i.[CompiledHash] AND r.[Checker] <> i.[EditedBy])))
        THROW 51305, 'A matrix version becomes active only by an approved request naming it, decided by someone who did not write it (DL-090).', 1;
END;");
            migrationBuilder.Sql(@"CREATE TRIGGER [policy].[TR_MatrixChangeRequest_DecidedImmutable] ON [policy].[MatrixChangeRequest]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51306, 'policy.MatrixChangeRequest rows are immutable and cannot be deleted (DL-090).', 1;
    IF UPDATE([Id]) OR UPDATE([MatrixId]) OR UPDATE([VersionId]) OR UPDATE([VersionNumber]) OR UPDATE([ContentHash]) OR UPDATE([CompiledHash]) OR UPDATE([Maker]) OR UPDATE([Reason]) OR UPDATE([ProposedAtUtc])
        THROW 51307, 'policy.MatrixChangeRequest proposal fields are immutable (DL-090).', 1;
    IF EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Proposed')
        THROW 51308, 'Decided policy.MatrixChangeRequest rows are immutable (DL-090).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the policy schema's guards; only valid on a database that holds no matrix worth keeping (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [policy].[TR_MatrixChangeRequest_DecidedImmutable];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [policy].[TR_MatrixVersion_Immutable];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [policy].[TR_ApprovalMatrix_Immutable];");
            migrationBuilder.Sql(@"ALTER TABLE [policy].[MatrixVersion] DROP CONSTRAINT [FK_MatrixVersion_Request_Names];");
            migrationBuilder.DropForeignKey(
                name: "FK_MatrixChangeRequest_ApprovalMatrix_MatrixId",
                schema: "policy",
                table: "MatrixChangeRequest");

            migrationBuilder.DropForeignKey(
                name: "FK_MatrixVersion_ApprovalMatrix_MatrixId",
                schema: "policy",
                table: "MatrixVersion");

            migrationBuilder.DropForeignKey(
                name: "FK_MatrixChangeRequest_MatrixVersion_VersionId",
                schema: "policy",
                table: "MatrixChangeRequest");

            migrationBuilder.DropTable(
                name: "ApprovalMatrix",
                schema: "policy");

            migrationBuilder.DropTable(
                name: "MatrixVersion",
                schema: "policy");

            migrationBuilder.DropTable(
                name: "MatrixChangeRequest",
                schema: "policy");
        }
    }
}
