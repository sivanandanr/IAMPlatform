using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Workflow
{
    /// <inheritdoc />
    public partial class WorkflowOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "workflow",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CausationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true, collation: "Latin1_General_100_BIN2"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
                    DispatchedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessage", x => x.MessageId);
                    table.CheckConstraint("CK_OutboxMessage_CausationId_Code", "DATALENGTH([CausationId]) > 0 AND DATALENGTH([CausationId]) = DATALENGTH(LTRIM(RTRIM([CausationId])))");
                    table.CheckConstraint("CK_OutboxMessage_CorrelationId_Code", "DATALENGTH([CorrelationId]) > 0 AND DATALENGTH([CorrelationId]) = DATALENGTH(LTRIM(RTRIM([CorrelationId])))");
                    table.CheckConstraint("CK_OutboxMessage_IdempotencyKey_Code", "DATALENGTH([IdempotencyKey]) > 0 AND DATALENGTH([IdempotencyKey]) = DATALENGTH(LTRIM(RTRIM([IdempotencyKey])))");
                    table.CheckConstraint("CK_OutboxMessage_LastError_Code", "DATALENGTH([LastError]) > 0 AND DATALENGTH([LastError]) = DATALENGTH(LTRIM(RTRIM([LastError])))");
                    table.CheckConstraint("CK_OutboxMessage_Source_Code", "DATALENGTH([Source]) > 0 AND DATALENGTH([Source]) = DATALENGTH(LTRIM(RTRIM([Source])))");
                    table.CheckConstraint("CK_OutboxMessage_Type_Code", "DATALENGTH([Type]) > 0 AND DATALENGTH([Type]) = DATALENGTH(LTRIM(RTRIM([Type])))");
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_DispatchedAtUtc_OccurredAtUtc",
                schema: "workflow",
                table: "OutboxMessage",
                columns: new[] { "DispatchedAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_IdempotencyKey",
                schema: "workflow",
                table: "OutboxMessage",
                column: "IdempotencyKey",
                unique: true);

            // DL-076: what a message says never changes, nothing is deleted, a sent message stays sent, attempts never go down.
            migrationBuilder.Sql(@"ALTER TABLE [workflow].[OutboxMessage] ADD CONSTRAINT [CK_OutboxMessage_Numbers] CHECK ([SchemaVersion] BETWEEN 1 AND 1000 AND [Attempts] >= 0 AND LEN([Payload]) > 1);");
            migrationBuilder.Sql(@"CREATE TRIGGER [workflow].[TR_OutboxMessage_Guarded] ON [workflow].[OutboxMessage]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
        THROW 51310, 'workflow.OutboxMessage rows cannot be deleted (DL-076).', 1;
    IF UPDATE([MessageId]) OR EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[MessageId] = i.[MessageId]
            WHERE i.[Type] <> d.[Type] OR i.[SchemaVersion] <> d.[SchemaVersion] OR i.[Source] <> d.[Source]
               OR i.[CorrelationId] <> d.[CorrelationId] OR ISNULL(i.[CausationId], N'') <> ISNULL(d.[CausationId], N'')
               OR i.[IdempotencyKey] <> d.[IdempotencyKey] OR i.[OccurredAtUtc] <> d.[OccurredAtUtc]
               OR CAST(i.[Payload] AS varbinary(max)) <> CAST(d.[Payload] AS varbinary(max))
               OR i.[CreatedAtUtc] <> d.[CreatedAtUtc] OR i.[CreatedBy] <> d.[CreatedBy])
        THROW 51311, 'An outbox message''s content is immutable (DL-076).', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[MessageId] = i.[MessageId]
            WHERE d.[DispatchedAtUtc] IS NOT NULL AND (i.[DispatchedAtUtc] IS NULL OR i.[DispatchedAtUtc] <> d.[DispatchedAtUtc]))
        THROW 51312, 'A dispatched outbox message stays dispatched (DL-076).', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[MessageId] = i.[MessageId] WHERE i.[Attempts] < d.[Attempts])
        THROW 51313, 'Outbox delivery attempts never go down (DL-076).', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [workflow].[TR_OutboxMessage_Guarded];");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "workflow");
        }
    }
}
