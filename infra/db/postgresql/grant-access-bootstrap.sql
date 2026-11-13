-- P0-013 slice 5 (DL-063a): a dedicated, short-lived login for the one-time access-control bootstrap on PostgreSQL.
-- Run as a superuser or the database owner, connected to the IAM database, after the Administration (`configuration`)
-- migration and grant-audit-role.sql (so iam_audit_writer exists). The login expires on its own after two hours; still
-- revoke it with revoke-access-bootstrap.sql straight after the bootstrap. Runbook: docs/runbooks/ACCESS_CONTROL_BOOTSTRAP.md.
--
-- Inputs, passed as session settings bound as parameters by the caller (never concatenated), for example
--   SELECT set_config('iam_bootstrap.login', $1, false), set_config('iam_bootstrap.password', $2, false)
--   iam_bootstrap.login     must start with iam_bootstrap (the tool refuses any other login)
--   iam_bootstrap.password  at least 16 characters; generate it for this one run and don't store it
--
-- Grants only what the bootstrap needs: read the configuration schema, insert the default roles, their actions, the two
-- assignments, the incompatible pairs and version 1, and append its audit event. No UPDATE or DELETE anywhere.
DO $grant$
DECLARE
    v_login    text := current_setting('iam_bootstrap.login');
    v_password text := current_setting('iam_bootstrap.password');
BEGIN
    IF v_login !~ '^iam_bootstrap[a-z0-9_]{0,50}$' THEN
        RAISE EXCEPTION 'The bootstrap login name must start with iam_bootstrap and use a-z, 0-9 and _ only.';
    END IF;
    IF v_password IS NULL OR length(v_password) < 16 THEN
        RAISE EXCEPTION 'The bootstrap password must be at least 16 characters.';
    END IF;
    IF to_regclass('configuration.access_model_version') IS NULL THEN
        RAISE EXCEPTION 'The configuration schema is not migrated yet; run the AccessControl migration first.';
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'iam_audit_writer') THEN
        RAISE EXCEPTION 'Role iam_audit_writer does not exist; run grant-audit-role.sql first.';
    END IF;

    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = v_login) THEN
        EXECUTE format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD %L VALID UNTIL %L',
            v_login, v_password, (now() + interval '2 hours')::text);
    ELSE
        EXECUTE format('ALTER ROLE %I LOGIN PASSWORD %L VALID UNTIL %L', v_login, v_password, (now() + interval '2 hours')::text);
    END IF;

    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), v_login);
    EXECUTE format('GRANT USAGE ON SCHEMA configuration TO %I', v_login);
    EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA configuration TO %I', v_login);
    EXECUTE format('GRANT INSERT ON configuration.access_role, configuration.access_role_action, configuration.access_role_assignment, '
        || 'configuration.access_incompatible_role_pair, configuration.access_model_version TO %I', v_login);
    EXECUTE format('GRANT iam_audit_writer TO %I WITH INHERIT TRUE, SET FALSE', v_login);
END
$grant$;
