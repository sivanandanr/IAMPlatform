using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Workflow
{
    /// <inheritdoc />
    public partial class WorkflowTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalTask",
                schema: "workflow",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Person = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RequestItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalTask", x => new { x.InstanceId, x.StageId, x.Person });
                    table.CheckConstraint("CK_ApprovalTask_Kind_Code", "DATALENGTH([Kind]) > 0 AND DATALENGTH([Kind]) = DATALENGTH(LTRIM(RTRIM([Kind])))");
                    table.CheckConstraint("CK_ApprovalTask_Person_Code", "DATALENGTH([Person]) > 0 AND DATALENGTH([Person]) = DATALENGTH(LTRIM(RTRIM([Person])))");
                    table.ForeignKey(
                        name: "FK_ApprovalTask_ApprovalInstance_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "workflow",
                        principalTable: "ApprovalInstance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTask_Person_DeadlineUtc",
                schema: "workflow",
                table: "ApprovalTask",
                columns: new[] { "Person", "DeadlineUtc" });

            // The inbox is a lookup rebuilt with every action (DL-073b): no append-only rules, only its shape is checked.
            migrationBuilder.Sql(@"ALTER TABLE [workflow].[ApprovalTask] ADD CONSTRAINT [CK_ApprovalTask_Kind] CHECK ([Kind] IN (N'Decision', N'Action') AND [Sequence] >= 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalTask",
                schema: "workflow");
        }
    }
}
