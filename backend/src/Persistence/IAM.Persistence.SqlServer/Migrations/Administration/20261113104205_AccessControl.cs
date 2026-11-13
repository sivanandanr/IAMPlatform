using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Administration
{
    /// <inheritdoc />
    public partial class AccessControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "configuration");

            migrationBuilder.CreateTable(
                name: "AccessChangeRequest",
                schema: "configuration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Payload = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Maker = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ProposedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ProposedAgainstVersion = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Checker = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2"),
                    DecisionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
                    AppliedAsVersion = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessChangeRequest", x => x.Id);
                    table.CheckConstraint("CK_AccessChangeRequest_Checker_Code", "DATALENGTH([Checker]) > 0 AND DATALENGTH([Checker]) = DATALENGTH(LTRIM(RTRIM([Checker])))");
                    table.CheckConstraint("CK_AccessChangeRequest_Kind_Code", "DATALENGTH([Kind]) > 0 AND DATALENGTH([Kind]) = DATALENGTH(LTRIM(RTRIM([Kind])))");
                    table.CheckConstraint("CK_AccessChangeRequest_Maker_Code", "DATALENGTH([Maker]) > 0 AND DATALENGTH([Maker]) = DATALENGTH(LTRIM(RTRIM([Maker])))");
                    table.CheckConstraint("CK_AccessChangeRequest_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                });

            migrationBuilder.CreateTable(
                name: "AccessRole",
                schema: "configuration",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Assignable = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRole", x => x.Code);
                    table.CheckConstraint("CK_AccessRole_Code_Code", "DATALENGTH([Code]) > 0 AND DATALENGTH([Code]) = DATALENGTH(LTRIM(RTRIM([Code])))");
                });

            migrationBuilder.CreateTable(
                name: "AccessModelVersion",
                schema: "configuration",
                columns: table => new
                {
                    ModelVersion = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ChangeRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModelHash = table.Column<string>(type: "nvarchar(67)", maxLength: 67, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AppliedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AppliedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessModelVersion", x => x.ModelVersion);
                    table.CheckConstraint("CK_AccessModelVersion_Kind_Code", "DATALENGTH([Kind]) > 0 AND DATALENGTH([Kind]) = DATALENGTH(LTRIM(RTRIM([Kind])))");
                    table.CheckConstraint("CK_AccessModelVersion_ModelHash_Code", "DATALENGTH([ModelHash]) > 0 AND DATALENGTH([ModelHash]) = DATALENGTH(LTRIM(RTRIM([ModelHash])))");
                    table.ForeignKey(
                        name: "FK_AccessModelVersion_AccessChangeRequest_ChangeRequestId",
                        column: x => x.ChangeRequestId,
                        principalSchema: "configuration",
                        principalTable: "AccessChangeRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessIncompatibleRolePair",
                schema: "configuration",
                columns: table => new
                {
                    First = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Second = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessIncompatibleRolePair", x => new { x.First, x.Second });
                    table.CheckConstraint("CK_AccessIncompatibleRolePair_First_Code", "DATALENGTH([First]) > 0 AND DATALENGTH([First]) = DATALENGTH(LTRIM(RTRIM([First])))");
                    table.CheckConstraint("CK_AccessIncompatibleRolePair_Second_Code", "DATALENGTH([Second]) > 0 AND DATALENGTH([Second]) = DATALENGTH(LTRIM(RTRIM([Second])))");
                    table.ForeignKey(
                        name: "FK_AccessIncompatibleRolePair_AccessRole_First",
                        column: x => x.First,
                        principalSchema: "configuration",
                        principalTable: "AccessRole",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessIncompatibleRolePair_AccessRole_Second",
                        column: x => x.Second,
                        principalSchema: "configuration",
                        principalTable: "AccessRole",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessRoleAction",
                schema: "configuration",
                columns: table => new
                {
                    RoleCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRoleAction", x => new { x.RoleCode, x.ActionId });
                    table.CheckConstraint("CK_AccessRoleAction_ActionId_Code", "DATALENGTH([ActionId]) > 0 AND DATALENGTH([ActionId]) = DATALENGTH(LTRIM(RTRIM([ActionId])))");
                    table.CheckConstraint("CK_AccessRoleAction_RoleCode_Code", "DATALENGTH([RoleCode]) > 0 AND DATALENGTH([RoleCode]) = DATALENGTH(LTRIM(RTRIM([RoleCode])))");
                    table.ForeignKey(
                        name: "FK_AccessRoleAction_AccessRole_RoleCode",
                        column: x => x.RoleCode,
                        principalSchema: "configuration",
                        principalTable: "AccessRole",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessRoleAssignment",
                schema: "configuration",
                columns: table => new
                {
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RoleCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRoleAssignment", x => new { x.Subject, x.RoleCode });
                    table.CheckConstraint("CK_AccessRoleAssignment_RoleCode_Code", "DATALENGTH([RoleCode]) > 0 AND DATALENGTH([RoleCode]) = DATALENGTH(LTRIM(RTRIM([RoleCode])))");
                    table.CheckConstraint("CK_AccessRoleAssignment_Subject_Code", "DATALENGTH([Subject]) > 0 AND DATALENGTH([Subject]) = DATALENGTH(LTRIM(RTRIM([Subject])))");
                    table.ForeignKey(
                        name: "FK_AccessRoleAssignment_AccessRole_RoleCode",
                        column: x => x.RoleCode,
                        principalSchema: "configuration",
                        principalTable: "AccessRole",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessChangeRequest_State_ProposedAtUtc",
                schema: "configuration",
                table: "AccessChangeRequest",
                columns: new[] { "State", "ProposedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessIncompatibleRolePair_Second",
                schema: "configuration",
                table: "AccessIncompatibleRolePair",
                column: "Second");

            migrationBuilder.CreateIndex(
                name: "IX_AccessModelVersion_ChangeRequestId",
                schema: "configuration",
                table: "AccessModelVersion",
                column: "ChangeRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoleAssignment_RoleCode",
                schema: "configuration",
                table: "AccessRoleAssignment",
                column: "RoleCode");
            // DL-059 / P0-013 hand-written and reviewed (not generated). Trigger names match the HasTrigger declarations in
            // AdministrationDbContext. Errors carry "immutable" or "proposed". The CHECKs repeat the domain's own state rules,
            // so a row written by SQL outside the application still can't describe an impossible change. Every comparison
            // has its own IS NOT NULL: a CHECK that evaluates to UNKNOWN passes.
            migrationBuilder.Sql("ALTER TABLE [configuration].[AccessChangeRequest] ADD CONSTRAINT [CK_AccessChangeRequest_Kind_Known] CHECK ([Kind] IN (N'AssignRole', N'RemoveAssignment', N'DefineRole', N'AddIncompatiblePair', N'RemoveIncompatiblePair'));");
            migrationBuilder.Sql("ALTER TABLE [configuration].[AccessChangeRequest] ADD CONSTRAINT [CK_AccessChangeRequest_Payload_Json] CHECK (ISJSON([Payload], OBJECT) = 1);");
            migrationBuilder.Sql("ALTER TABLE [configuration].[AccessChangeRequest] ADD CONSTRAINT [CK_AccessChangeRequest_ProposedAgainstVersion] CHECK ([ProposedAgainstVersion] >= 0);");
            migrationBuilder.Sql(@"ALTER TABLE [configuration].[AccessChangeRequest] ADD CONSTRAINT [CK_AccessChangeRequest_State] CHECK (
    ([State] = N'Proposed' AND [Checker] IS NULL AND [DecisionReason] IS NULL AND [DecidedAtUtc] IS NULL AND [AppliedAsVersion] IS NULL)
    OR ([State] = N'Withdrawn' AND [Checker] IS NOT NULL AND [Checker] = [Maker] AND [DecidedAtUtc] IS NOT NULL AND [AppliedAsVersion] IS NULL)
    OR ([State] = N'Rejected' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecisionReason] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [AppliedAsVersion] IS NULL)
    OR ([State] = N'Approved' AND [Checker] IS NOT NULL AND [Checker] <> [Maker] AND [DecidedAtUtc] IS NOT NULL AND [AppliedAsVersion] IS NOT NULL AND [AppliedAsVersion] > [ProposedAgainstVersion]));");

            // Exactly one bootstrap version (number 1, no change request); every later version names the change that made it.
            migrationBuilder.Sql(@"ALTER TABLE [configuration].[AccessModelVersion] ADD CONSTRAINT [CK_AccessModelVersion_Kind] CHECK (
    ([Kind] = N'Bootstrap' AND [ModelVersion] = 1 AND [ChangeRequestId] IS NULL)
    OR ([Kind] = N'Change' AND [ModelVersion] > 1 AND [ChangeRequestId] IS NOT NULL));");
            migrationBuilder.Sql("ALTER TABLE [configuration].[AccessModelVersion] ADD CONSTRAINT [CK_AccessModelVersion_ModelHash_Hex] CHECK (LEN([ModelHash]) = 67 AND LEFT([ModelHash], 3) = N'v1:' AND SUBSTRING([ModelHash], 4, 64) NOT LIKE '%[^0-9a-f]%');");

            // An approved request names the version it produced, and that version names it back: the pair must match. Also
            // makes both tables impossible to TRUNCATE, since each is referenced by a foreign key.
            migrationBuilder.Sql("ALTER TABLE [configuration].[AccessModelVersion] ADD CONSTRAINT [AK_AccessModelVersion_ModelVersion_ChangeRequestId] UNIQUE ([ModelVersion], [ChangeRequestId]);");
            migrationBuilder.Sql("ALTER TABLE [configuration].[AccessChangeRequest] ADD CONSTRAINT [FK_AccessChangeRequest_AccessModelVersion_Applied] FOREIGN KEY ([AppliedAsVersion], [Id]) REFERENCES [configuration].[AccessModelVersion] ([ModelVersion], [ChangeRequestId]);");

            // Applied versions are history: never changed, never removed.
            migrationBuilder.Sql(@"CREATE TRIGGER [configuration].[TR_AccessModelVersion_AppendOnly] ON [configuration].[AccessModelVersion]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51020, 'configuration.AccessModelVersion rows are immutable (DL-059).', 1;
END;");

            // A version can only be claimed for a request that is still proposed. UPDLOCK holds the request row until commit,
            // so a reject or withdraw can't commit between this check and the approval (it waits, then sees Approved).
            migrationBuilder.Sql(@"CREATE TRIGGER [configuration].[TR_AccessModelVersion_ClaimsProposed] ON [configuration].[AccessModelVersion]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted i
        JOIN [configuration].[AccessChangeRequest] r WITH (UPDLOCK, ROWLOCK, READCOMMITTEDLOCK) ON r.[Id] = i.[ChangeRequestId]
        WHERE r.[State] <> N'Proposed')
        THROW 51024, 'A model version can only be claimed for a proposed change request (DL-059).', 1;
END;");

            // What was proposed, by whom and why never changes; a decided request never changes; no request is removed.
            migrationBuilder.Sql(@"CREATE TRIGGER [configuration].[TR_AccessChangeRequest_DecidedImmutable] ON [configuration].[AccessChangeRequest]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 51021, 'configuration.AccessChangeRequest rows are immutable and cannot be deleted (DL-059).', 1;
    IF UPDATE([Id]) OR UPDATE([Kind]) OR UPDATE([Payload]) OR UPDATE([Maker]) OR UPDATE([Reason]) OR UPDATE([ProposedAtUtc]) OR UPDATE([ProposedAgainstVersion])
        THROW 51022, 'configuration.AccessChangeRequest proposal fields are immutable (DL-059).', 1;
    IF EXISTS (SELECT 1 FROM deleted WHERE [State] <> N'Proposed')
        THROW 51023, 'Decided configuration.AccessChangeRequest rows are immutable (DL-059).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE [configuration].[AccessChangeRequest] DROP CONSTRAINT [FK_AccessChangeRequest_AccessModelVersion_Applied];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [configuration].[TR_AccessModelVersion_AppendOnly];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [configuration].[TR_AccessModelVersion_ClaimsProposed];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [configuration].[TR_AccessChangeRequest_DecidedImmutable];");

            migrationBuilder.DropTable(
                name: "AccessIncompatibleRolePair",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "AccessModelVersion",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "AccessRoleAction",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "AccessRoleAssignment",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "AccessChangeRequest",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "AccessRole",
                schema: "configuration");
        }
    }
}
