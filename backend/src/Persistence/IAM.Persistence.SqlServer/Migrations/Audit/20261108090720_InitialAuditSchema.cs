using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Audit
{
    /// <inheritdoc />
    public partial class InitialAuditSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "AuditEvent",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EffectiveActorId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TargetType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TargetId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CausationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true, collation: "Latin1_General_100_BIN2"),
                    PolicyVersionRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PolicyVersionHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    PayloadCanonical = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvent", x => new { x.RecordedAtUtc, x.Id });
                    table.CheckConstraint("CK_AuditEvent_Action_Code", "DATALENGTH([Action]) > 0 AND DATALENGTH([Action]) = DATALENGTH(LTRIM(RTRIM([Action])))");
                    table.CheckConstraint("CK_AuditEvent_CausationId_Code", "DATALENGTH([CausationId]) > 0 AND DATALENGTH([CausationId]) = DATALENGTH(LTRIM(RTRIM([CausationId])))");
                    table.CheckConstraint("CK_AuditEvent_CorrelationId_Code", "DATALENGTH([CorrelationId]) > 0 AND DATALENGTH([CorrelationId]) = DATALENGTH(LTRIM(RTRIM([CorrelationId])))");
                    table.CheckConstraint("CK_AuditEvent_Outcome_Code", "DATALENGTH([Outcome]) > 0 AND DATALENGTH([Outcome]) = DATALENGTH(LTRIM(RTRIM([Outcome])))");
                    table.CheckConstraint("CK_AuditEvent_PolicyVersionHash_Code", "DATALENGTH([PolicyVersionHash]) > 0 AND DATALENGTH([PolicyVersionHash]) = DATALENGTH(LTRIM(RTRIM([PolicyVersionHash])))");
                    table.CheckConstraint("CK_AuditEvent_RowHash_Code", "DATALENGTH([RowHash]) > 0 AND DATALENGTH([RowHash]) = DATALENGTH(LTRIM(RTRIM([RowHash])))");
                    table.CheckConstraint("CK_AuditEvent_TargetType_Code", "DATALENGTH([TargetType]) > 0 AND DATALENGTH([TargetType]) = DATALENGTH(LTRIM(RTRIM([TargetType])))");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_CorrelationId",
                schema: "audit",
                table: "AuditEvent",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_Id",
                schema: "audit",
                table: "AuditEvent",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_Seq",
                schema: "audit",
                table: "AuditEvent",
                column: "Seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_TargetType_TargetId",
                schema: "audit",
                table: "AuditEvent",
                columns: new[] { "TargetType", "TargetId" });

            // ADR-0018 append-only enforcement, hand-written and reviewed (not generated). Requires roles
            // iam_audit_writer and iam_audit_reader from infra/db/sqlserver/grant-audit-role.sql; fails closed without them.
            // Grants are on this table only, never schema-wide, so the migration history stays migrator-only.
            // (SQL Server has no column-level INSERT permission; supplying Seq needs IDENTITY_INSERT, i.e. ALTER, which
            // the grant script denies.)
            migrationBuilder.Sql("GRANT INSERT ON [audit].[AuditEvent] TO [iam_audit_writer];");
            migrationBuilder.Sql("GRANT SELECT ON [audit].[AuditEvent] TO [iam_audit_reader];");
            migrationBuilder.Sql("DENY UPDATE, DELETE ON [audit].[AuditEvent] TO [iam_audit_writer], [iam_audit_reader];");

            // Second layer that also binds the schema owner: any UPDATE or DELETE fails with error 51000, distinct from
            // the permission errors (229/230) the grants raise. TRUNCATE does not fire triggers on SQL Server; role
            // members are denied the ALTER it needs. Residual risk R-026.
            migrationBuilder.Sql(@"CREATE TRIGGER [audit].[TR_AuditEvent_AppendOnly] ON [audit].[AuditEvent]
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'audit.AuditEvent is append-only: UPDATE and DELETE are not allowed (ADR-0018).', 1;
END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never drop the audit store: that would destroy evidence (ADR-0018). Roll back with a compensating forward
            // migration and, for data, restore/PITR (DL-020).
            throw new NotSupportedException("The audit schema cannot be rolled back by migration (ADR-0018).");
        }
    }
}
