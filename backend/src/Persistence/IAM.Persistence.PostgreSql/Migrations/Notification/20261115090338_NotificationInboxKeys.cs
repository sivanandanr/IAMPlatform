using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Notification
{
    /// <inheritdoc />
    public partial class NotificationInboxKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                schema: "notification",
                table: "inbox_message",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "source",
                schema: "notification",
                table: "inbox_message",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "",
                collation: "C");

            // DL-079: rows from before this change came from the workflow; their message id stands in as their key. The
            // append-only trigger is paused for this one update only, inside the migration's transaction.
            migrationBuilder.Sql(@"ALTER TABLE notification.inbox_message DISABLE TRIGGER tr_inbox_message_append_only;
UPDATE notification.inbox_message SET source = 'workflow', idempotency_key = message_id::text;
ALTER TABLE notification.inbox_message ENABLE ALWAYS TRIGGER tr_inbox_message_append_only;");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_message_consumer_name_source_idempotency_key",
                schema: "notification",
                table: "inbox_message",
                columns: new[] { "consumer_name", "source", "idempotency_key" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_message_idempotency_key_code",
                schema: "notification",
                table: "inbox_message",
                sql: "\"idempotency_key\" <> '' AND \"idempotency_key\" = btrim(\"idempotency_key\")");

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_message_source_code",
                schema: "notification",
                table: "inbox_message",
                sql: "\"source\" <> '' AND \"source\" = btrim(\"source\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_inbox_message_consumer_name_source_idempotency_key",
                schema: "notification",
                table: "inbox_message");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_message_idempotency_key_code",
                schema: "notification",
                table: "inbox_message");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_message_source_code",
                schema: "notification",
                table: "inbox_message");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                schema: "notification",
                table: "inbox_message");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "notification",
                table: "inbox_message");
        }
    }
}
