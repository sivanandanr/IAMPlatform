using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Request
{
    /// <inheritdoc />
    public partial class AccountNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountNameAssignment",
                schema: "request",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SetBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SetAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountNameAssignment", x => x.Id);
                    table.CheckConstraint("CK_AccountNameAssignment_Name_Code", "DATALENGTH([Name]) > 0 AND DATALENGTH([Name]) = DATALENGTH(LTRIM(RTRIM([Name])))");
                    table.CheckConstraint("CK_AccountNameAssignment_PreviousName_Code", "DATALENGTH([PreviousName]) > 0 AND DATALENGTH([PreviousName]) = DATALENGTH(LTRIM(RTRIM([PreviousName])))");
                    table.CheckConstraint("CK_AccountNameAssignment_SetBy_Code", "DATALENGTH([SetBy]) > 0 AND DATALENGTH([SetBy]) = DATALENGTH(LTRIM(RTRIM([SetBy])))");
                    table.ForeignKey(
                        name: "FK_AccountNameAssignment_RequestItem_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "request",
                        principalTable: "RequestItem",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountNameAssignment_ItemId_SetAtUtc",
                schema: "request",
                table: "AccountNameAssignment",
                columns: new[] { "ItemId", "SetAtUtc" });
            // DL-092, hand-written and reviewed (not generated). A name a workflow level gave a new account is append-only: the
            // latest one is the name, and none is ever changed or deleted. Names are checked as accounts' are (hyphen first in the
            // SQL Server pattern, DL-091).
            migrationBuilder.Sql(@"ALTER TABLE [request].[AccountNameAssignment] ADD CONSTRAINT [CK_AccountNameAssignment_Names] CHECK (LEN([Name]) BETWEEN 1 AND 256 AND DATALENGTH([Name]) = 2 * LEN([Name]) AND [Name] NOT LIKE '[-._]%' AND [Name] NOT LIKE '%[^-A-Za-z0-9._]%' AND ([PreviousName] IS NULL OR ([PreviousName] NOT LIKE '[-._]%' AND [PreviousName] NOT LIKE '%[^-A-Za-z0-9._]%' AND [PreviousName] <> [Name])));");
            migrationBuilder.Sql(@"CREATE TRIGGER [request].[TR_AccountNameAssignment_AppendOnly] ON [request].[AccountNameAssignment]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51402, 'request.AccountNameAssignment rows are append-only (DL-092).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [request].[TR_AccountNameAssignment_AppendOnly];");
            migrationBuilder.DropTable(
                name: "AccountNameAssignment",
                schema: "request");
        }
    }
}
