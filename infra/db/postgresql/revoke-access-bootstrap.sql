-- P0-013 slice 5 (DL-063a): removes the bootstrap login straight after the one-time bootstrap. Run as a superuser or
-- the database owner, connected to the IAM database. Idempotent.
--
-- Input, passed as a session setting bound as a parameter by the caller:
--   SELECT set_config('iam_bootstrap.login', $1, false)
DO $revoke$
DECLARE
    v_login text := current_setting('iam_bootstrap.login');
BEGIN
    IF v_login !~ '^iam_bootstrap[a-z0-9_]{0,50}$' THEN
        RAISE EXCEPTION 'The bootstrap login name must start with iam_bootstrap and use a-z, 0-9 and _ only.';
    END IF;
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = v_login) THEN
        EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA configuration FROM %I', v_login);
        EXECUTE format('REVOKE ALL ON SCHEMA configuration FROM %I', v_login);
        EXECUTE format('REVOKE iam_audit_writer FROM %I', v_login);
        EXECUTE format('REVOKE CONNECT ON DATABASE %I FROM %I', current_database(), v_login);
        EXECUTE format('DROP ROLE %I', v_login);
    END IF;
END
$revoke$;
