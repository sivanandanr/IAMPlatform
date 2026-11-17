using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Request
{
    /// <inheritdoc />
    public partial class RequestItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "request");

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "request",
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

            migrationBuilder.CreateTable(
                name: "RequestItem",
                schema: "request",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Requester = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Beneficiary = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntitlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntitlementVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntitlementContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NewAccountType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true, collation: "Latin1_General_100_BIN2"),
                    NewAccountName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    DurationDays = table.Column<int>(type: "int", nullable: true),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MatrixVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MatrixContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MatchedRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyMaker = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Evidence = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Route = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestItem", x => x.Id);
                    table.CheckConstraint("CK_RequestItem_Beneficiary_Code", "DATALENGTH([Beneficiary]) > 0 AND DATALENGTH([Beneficiary]) = DATALENGTH(LTRIM(RTRIM([Beneficiary])))");
                    table.CheckConstraint("CK_RequestItem_EntitlementContentHash_Code", "DATALENGTH([EntitlementContentHash]) > 0 AND DATALENGTH([EntitlementContentHash]) = DATALENGTH(LTRIM(RTRIM([EntitlementContentHash])))");
                    table.CheckConstraint("CK_RequestItem_MatrixContentHash_Code", "DATALENGTH([MatrixContentHash]) > 0 AND DATALENGTH([MatrixContentHash]) = DATALENGTH(LTRIM(RTRIM([MatrixContentHash])))");
                    table.CheckConstraint("CK_RequestItem_NewAccountName_Code", "DATALENGTH([NewAccountName]) > 0 AND DATALENGTH([NewAccountName]) = DATALENGTH(LTRIM(RTRIM([NewAccountName])))");
                    table.CheckConstraint("CK_RequestItem_NewAccountType_Code", "DATALENGTH([NewAccountType]) > 0 AND DATALENGTH([NewAccountType]) = DATALENGTH(LTRIM(RTRIM([NewAccountType])))");
                    table.CheckConstraint("CK_RequestItem_PolicyMaker_Code", "DATALENGTH([PolicyMaker]) > 0 AND DATALENGTH([PolicyMaker]) = DATALENGTH(LTRIM(RTRIM([PolicyMaker])))");
                    table.CheckConstraint("CK_RequestItem_Requester_Code", "DATALENGTH([Requester]) > 0 AND DATALENGTH([Requester]) = DATALENGTH(LTRIM(RTRIM([Requester])))");
                    table.CheckConstraint("CK_RequestItem_State_Code", "DATALENGTH([State]) > 0 AND DATALENGTH([State]) = DATALENGTH(LTRIM(RTRIM([State])))");
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_DispatchedAtUtc_OccurredAtUtc",
                schema: "request",
                table: "OutboxMessage",
                columns: new[] { "DispatchedAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_IdempotencyKey",
                schema: "request",
                table: "OutboxMessage",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestItem_Beneficiary_SubmittedAtUtc",
                schema: "request",
                table: "RequestItem",
                columns: new[] { "Beneficiary", "SubmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RequestItem_Requester_SubmittedAtUtc",
                schema: "request",
                table: "RequestItem",
                columns: new[] { "Requester", "SubmittedAtUtc" });
            // DL-091, hand-written and reviewed (not generated). A request item is frozen when submitted: nothing changes it and
            // nothing deletes it. One waiting item per person and entitlement. The outbox has the same guards as Workflow's
            // (DL-076, DL-079): what a message says never changes, nothing is deleted, a sent message stays sent.
            migrationBuilder.Sql(@"ALTER TABLE [request].[RequestItem] ADD CONSTRAINT [CK_RequestItem_Shape] CHECK ([State] IN (N'Submitted') AND ((([AccountId] IS NULL) AND [NewAccountType] IN (N'Primary', N'Secondary', N'Service', N'Shared')) OR ([AccountId] IS NOT NULL AND [NewAccountType] IS NULL AND [NewAccountName] IS NULL)) AND ([DurationDays] IS NULL OR [DurationDays] BETWEEN 1 AND 3650));");
            migrationBuilder.Sql(@"ALTER TABLE [request].[RequestItem] ADD CONSTRAINT [CK_RequestItem_Hashes] CHECK (LEN([EntitlementContentHash]) = 64 AND [EntitlementContentHash] NOT LIKE '%[^0-9a-f]%' AND LEN([MatrixContentHash]) = 64 AND [MatrixContentHash] NOT LIKE '%[^0-9a-f]%');");
            migrationBuilder.Sql(@"ALTER TABLE [request].[RequestItem] ADD CONSTRAINT [CK_RequestItem_Name] CHECK ([NewAccountName] IS NULL OR (LEN([NewAccountName]) BETWEEN 1 AND 256 AND DATALENGTH([NewAccountName]) = 2 * LEN([NewAccountName]) AND [NewAccountName] NOT LIKE '[-._]%' AND [NewAccountName] NOT LIKE '%[^-A-Za-z0-9._]%'));");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX [UX_RequestItem_OneWaiting] ON [request].[RequestItem] ([Beneficiary], [EntitlementId]) WHERE [State] = N'Submitted';");
            migrationBuilder.Sql(@"CREATE TRIGGER [request].[TR_RequestItem_Frozen] ON [request].[RequestItem]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51401, 'request.RequestItem rows are frozen when submitted: they cannot be changed or deleted (DL-091).', 1;
END;");
            migrationBuilder.Sql(@"ALTER TABLE [request].[OutboxMessage] ADD CONSTRAINT [CK_OutboxMessage_Numbers] CHECK ([SchemaVersion] BETWEEN 1 AND 1000 AND [Attempts] >= 0 AND LEN([Payload]) > 1);");
            migrationBuilder.Sql(@"CREATE TRIGGER [request].[TR_OutboxMessage_Guarded] ON [request].[OutboxMessage]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
        THROW 51410, 'request.OutboxMessage rows cannot be deleted (DL-076).', 1;
    IF UPDATE([MessageId]) OR EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[MessageId] = i.[MessageId]
            WHERE i.[Type] <> d.[Type] OR i.[SchemaVersion] <> d.[SchemaVersion] OR i.[Source] <> d.[Source]
               OR i.[CorrelationId] <> d.[CorrelationId] OR ISNULL(i.[CausationId], N'') <> ISNULL(d.[CausationId], N'')
               OR i.[IdempotencyKey] <> d.[IdempotencyKey] OR i.[OccurredAtUtc] <> d.[OccurredAtUtc]
               OR CAST(i.[Payload] AS varbinary(max)) <> CAST(d.[Payload] AS varbinary(max))
               OR i.[CreatedAtUtc] <> d.[CreatedAtUtc] OR CAST(i.[CreatedBy] AS varbinary(400)) <> CAST(d.[CreatedBy] AS varbinary(400)))
        THROW 51411, 'An outbox message''s content is immutable (DL-076).', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[MessageId] = i.[MessageId]
            WHERE d.[DispatchedAtUtc] IS NOT NULL AND (i.[DispatchedAtUtc] IS NULL OR i.[DispatchedAtUtc] <> d.[DispatchedAtUtc]))
        THROW 51412, 'A dispatched outbox message stays dispatched (DL-076).', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[MessageId] = i.[MessageId] WHERE i.[Attempts] < d.[Attempts])
        THROW 51413, 'Outbox delivery attempts never go down (DL-076).', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the request schema's guards; only valid on a database that holds no requests worth keeping (DL-020).
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [request].[TR_OutboxMessage_Guarded];");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [request].[TR_RequestItem_Frozen];");
            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "request");

            migrationBuilder.DropTable(
                name: "RequestItem",
                schema: "request");
        }
    }
}
