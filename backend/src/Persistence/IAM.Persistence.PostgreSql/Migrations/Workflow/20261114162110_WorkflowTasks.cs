using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Workflow
{
    /// <inheritdoc />
    public partial class WorkflowTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approval_task",
                schema: "workflow",
                columns: table => new
                {
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    request_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    deadline_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_task", x => new { x.instance_id, x.stage_id, x.person });
                    table.CheckConstraint("ck_approval_task_kind_code", "\"kind\" <> '' AND \"kind\" = btrim(\"kind\")");
                    table.CheckConstraint("ck_approval_task_person_code", "\"person\" <> '' AND \"person\" = btrim(\"person\")");
                    table.ForeignKey(
                        name: "fk_approval_task_approval_instance_instance_id",
                        column: x => x.instance_id,
                        principalSchema: "workflow",
                        principalTable: "approval_instance",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approval_task_person_deadline_utc",
                schema: "workflow",
                table: "approval_task",
                columns: new[] { "person", "deadline_utc" });

            // The inbox is a lookup rebuilt with every action (DL-073b): no append-only rules, only its shape is checked.
            migrationBuilder.Sql(@"ALTER TABLE workflow.approval_task ADD CONSTRAINT ck_approval_task_kind CHECK (kind IN ('Decision', 'Action') AND sequence >= 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approval_task",
                schema: "workflow");
        }
    }
}
