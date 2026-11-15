using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Workflow
{
    /// <inheritdoc />
    public partial class WorkflowOutboxGuardFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-079: CreatedBy is compared byte for byte, as on PostgreSQL (its collation ignores case and trailing spaces).
            migrationBuilder.Sql(@"CREATE OR ALTER TRIGGER [workflow].[TR_OutboxMessage_Guarded] ON [workflow].[OutboxMessage]
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
               OR i.[CreatedAtUtc] <> d.[CreatedAtUtc] OR CAST(i.[CreatedBy] AS varbinary(400)) <> CAST(d.[CreatedBy] AS varbinary(400)))
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
            migrationBuilder.Sql(@"CREATE OR ALTER TRIGGER [workflow].[TR_OutboxMessage_Guarded] ON [workflow].[OutboxMessage]
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
    }
}
