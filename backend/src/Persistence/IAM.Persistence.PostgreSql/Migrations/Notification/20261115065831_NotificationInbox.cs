using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Notification
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
                name: "inbox_message",
                schema: "notification",
                columns: table => new
                {
                    consumer_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_message", x => new { x.consumer_name, x.message_id });
                    table.CheckConstraint("ck_inbox_message_consumer_name_code", "\"consumer_name\" <> '' AND \"consumer_name\" = btrim(\"consumer_name\")");
                    table.CheckConstraint("ck_inbox_message_type_code", "\"type\" <> '' AND \"type\" = btrim(\"type\")");
                });

            migrationBuilder.CreateTable(
                name: "pending_notification",
                schema: "notification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, collation: "C"),
                    recipient = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deadline_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: true),
                    source_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_for_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    modified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_notification", x => x.id);
                    table.CheckConstraint("ck_pending_notification_kind_code", "\"kind\" <> '' AND \"kind\" = btrim(\"kind\")");
                    table.CheckConstraint("ck_pending_notification_recipient_code", "\"recipient\" <> '' AND \"recipient\" = btrim(\"recipient\")");
                });

            migrationBuilder.CreateIndex(
                name: "ix_pending_notification_recipient_created_for_utc",
                schema: "notification",
                table: "pending_notification",
                columns: new[] { "recipient", "created_for_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_pending_notification_source_message_id_recipient",
                schema: "notification",
                table: "pending_notification",
                columns: new[] { "source_message_id", "recipient" },
                unique: true);

            // DL-078: the inbox is append-only, so a processed message can't be forgotten and processed again.
            migrationBuilder.Sql(@"CREATE FUNCTION notification.guard_inbox_message() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'notification.inbox_message rows are append-only (DL-078)';
END
$$;");
            migrationBuilder.Sql(@"REVOKE ALL ON FUNCTION notification.guard_inbox_message() FROM PUBLIC;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_inbox_message_append_only BEFORE UPDATE OR DELETE ON notification.inbox_message FOR EACH ROW EXECUTE FUNCTION notification.guard_inbox_message();");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_inbox_message_no_truncate BEFORE TRUNCATE ON notification.inbox_message FOR EACH STATEMENT EXECUTE FUNCTION notification.guard_inbox_message();");
            migrationBuilder.Sql(@"ALTER TABLE notification.inbox_message ENABLE ALWAYS TRIGGER tr_inbox_message_append_only;");
            migrationBuilder.Sql(@"ALTER TABLE notification.inbox_message ENABLE ALWAYS TRIGGER tr_inbox_message_no_truncate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_inbox_message_no_truncate ON notification.inbox_message;");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS tr_inbox_message_append_only ON notification.inbox_message;");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS notification.guard_inbox_message();");

            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "notification");

            migrationBuilder.DropTable(
                name: "pending_notification",
                schema: "notification");
        }
    }
}
