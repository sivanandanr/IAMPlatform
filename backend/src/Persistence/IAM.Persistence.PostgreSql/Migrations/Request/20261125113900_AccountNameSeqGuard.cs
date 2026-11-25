using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.PostgreSql.Migrations.Request
{
    /// <inheritdoc />
    public partial class AccountNameSeqGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-102: two names of one item written at the same moment leave "latest" open: the upgrade stops for a person.
            migrationBuilder.Sql(@"DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM request.account_name_assignment a
               JOIN request.account_name_assignment b ON a.item_id = b.item_id AND a.id <> b.id AND a.set_at_utc = b.set_at_utc) THEN
        RAISE EXCEPTION 'an item has two account names written at the same moment, so which is latest is unknown; resolve them before upgrading (DL-102)';
    END IF;
END $$;");

            // DL-102: GENERATED ALWAYS still lets a login with INSERT write seq by OVERRIDING SYSTEM VALUE. Every insert takes the
            // next value from the column's own sequence instead, whatever it said.
            migrationBuilder.Sql(@"CREATE FUNCTION request.take_account_name_seq() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    NEW.seq := nextval(pg_get_serial_sequence('request.account_name_assignment', 'seq'));
    RETURN NEW;
END
$$;");
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION request.take_account_name_seq() FROM PUBLIC;");
            migrationBuilder.Sql("CREATE TRIGGER tr_account_name_assignment_seq BEFORE INSERT ON request.account_name_assignment FOR EACH ROW EXECUTE FUNCTION request.take_account_name_seq();");
            migrationBuilder.Sql("ALTER TABLE request.account_name_assignment ENABLE ALWAYS TRIGGER tr_account_name_assignment_seq;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only (DL-020): Down is for an empty development database; with data, recover by restore and a compensating
            // forward migration.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_account_name_assignment_seq ON request.account_name_assignment;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS request.take_account_name_seq();");
        }
    }
}
