using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Notification
{
    /// <inheritdoc />
    public partial class NotificationInboxKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                schema: "notification",
                table: "InboxMessage",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "notification",
                table: "InboxMessage",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            // DL-079: rows from before this change came from the workflow; their message id stands in as their key. The
            // append-only trigger is paused for this one update only, inside the migration's transaction.
            migrationBuilder.Sql(@"DISABLE TRIGGER [notification].[TR_InboxMessage_AppendOnly] ON [notification].[InboxMessage];
UPDATE [notification].[InboxMessage] SET [Source] = N'workflow', [IdempotencyKey] = LOWER(CONVERT(nvarchar(36), [MessageId]));
ENABLE TRIGGER [notification].[TR_InboxMessage_AppendOnly] ON [notification].[InboxMessage];");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessage_ConsumerName_Source_IdempotencyKey",
                schema: "notification",
                table: "InboxMessage",
                columns: new[] { "ConsumerName", "Source", "IdempotencyKey" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_InboxMessage_IdempotencyKey_Code",
                schema: "notification",
                table: "InboxMessage",
                sql: "DATALENGTH([IdempotencyKey]) > 0 AND DATALENGTH([IdempotencyKey]) = DATALENGTH(LTRIM(RTRIM([IdempotencyKey])))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InboxMessage_Source_Code",
                schema: "notification",
                table: "InboxMessage",
                sql: "DATALENGTH([Source]) > 0 AND DATALENGTH([Source]) = DATALENGTH(LTRIM(RTRIM([Source])))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxMessage_ConsumerName_Source_IdempotencyKey",
                schema: "notification",
                table: "InboxMessage");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InboxMessage_IdempotencyKey_Code",
                schema: "notification",
                table: "InboxMessage");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InboxMessage_Source_Code",
                schema: "notification",
                table: "InboxMessage");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "notification",
                table: "InboxMessage");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "notification",
                table: "InboxMessage");
        }
    }
}
