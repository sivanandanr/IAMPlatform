using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Notification
{
    /// <inheritdoc />
    public partial class NotificationInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notification");

            migrationBuilder.CreateTable(
                name: "InboxMessage",
                schema: "notification",
                columns: table => new
                {
                    ConsumerName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessage", x => new { x.ConsumerName, x.MessageId });
                    table.CheckConstraint("CK_InboxMessage_ConsumerName_Code", "DATALENGTH([ConsumerName]) > 0 AND DATALENGTH([ConsumerName]) = DATALENGTH(LTRIM(RTRIM([ConsumerName])))");
                    table.CheckConstraint("CK_InboxMessage_Type_Code", "DATALENGTH([Type]) > 0 AND DATALENGTH([Type]) = DATALENGTH(LTRIM(RTRIM([Type])))");
                });

            migrationBuilder.CreateTable(
                name: "PendingNotification",
                schema: "notification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Recipient = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
                    SourceMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedForUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingNotification", x => x.Id);
                    table.CheckConstraint("CK_PendingNotification_Kind_Code", "DATALENGTH([Kind]) > 0 AND DATALENGTH([Kind]) = DATALENGTH(LTRIM(RTRIM([Kind])))");
                    table.CheckConstraint("CK_PendingNotification_Recipient_Code", "DATALENGTH([Recipient]) > 0 AND DATALENGTH([Recipient]) = DATALENGTH(LTRIM(RTRIM([Recipient])))");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingNotification_Recipient_CreatedForUtc",
                schema: "notification",
                table: "PendingNotification",
                columns: new[] { "Recipient", "CreatedForUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingNotification_SourceMessageId_Recipient",
                schema: "notification",
                table: "PendingNotification",
                columns: new[] { "SourceMessageId", "Recipient" },
                unique: true);

            // DL-078: the inbox is append-only, so a processed message can't be forgotten and processed again.
            migrationBuilder.Sql(@"CREATE TRIGGER [notification].[TR_InboxMessage_AppendOnly] ON [notification].[InboxMessage]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51401, 'notification.InboxMessage rows are append-only (DL-078).', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS [notification].[TR_InboxMessage_AppendOnly];");

            migrationBuilder.DropTable(
                name: "InboxMessage",
                schema: "notification");

            migrationBuilder.DropTable(
                name: "PendingNotification",
                schema: "notification");
        }
    }
}
