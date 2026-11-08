-- S1-07 audit role grant on PostgreSQL (ADR-0018). Run as a superuser or the database owner, connected to the IAM
-- database, after bootstrap-module.sql has created the 'audit' schema and the module role, and BEFORE the audit
-- migration (which grants to these roles and fails if they are missing). Idempotent. Needs PostgreSQL 16 or later.
--
-- Inputs, passed as session settings bound as parameters by the caller (never concatenated), for example
--   SELECT set_config('iam_bootstrap.app_login', $1, false), set_config('iam_bootstrap.audit_role', $2, false)
--   iam_bootstrap.app_login    an existing module runtime role
--   iam_bootstrap.audit_role   'iam_audit_writer' (every module runtime role, including the audit role) or
--                              'iam_audit_reader' (the audit role only)
--
-- Module roles are NOINHERIT, so membership is granted WITH INHERIT TRUE (PostgreSQL 16+) and SET FALSE (no SET ROLE).
-- The roles hold USAGE on schema audit only. The migration grants iam_audit_writer INSERT on every column of
-- audit.audit_event except seq, and iam_audit_reader SELECT on that table only (not on the migration history), and adds
-- triggers that reject UPDATE, DELETE and TRUNCATE. PostgreSQL has no DENY: nothing else is granted, so nothing else is
-- allowed.
DO $grant$
DECLARE
    v_app  text := current_setting('iam_bootstrap.app_login');
    v_role text := current_setting('iam_bootstrap.audit_role');
BEGIN
    IF current_setting('server_version_num')::int < 160000 THEN
        RAISE EXCEPTION 'PostgreSQL 16 or later is required.';
    END IF;
    IF v_app !~ '^[a-z][a-z0-9_]{0,62}$' THEN RAISE EXCEPTION 'Invalid application login name.'; END IF;
    IF v_role NOT IN ('iam_audit_writer', 'iam_audit_reader') THEN
        RAISE EXCEPTION 'Role must be iam_audit_writer or iam_audit_reader.';
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = v_app) THEN
        RAISE EXCEPTION 'The application role does not exist; run bootstrap-module.sql first.';
    END IF;
    IF NOT EXISTS (SELECT FROM pg_namespace WHERE nspname = 'audit') THEN
        RAISE EXCEPTION 'Schema audit does not exist; run bootstrap-module.sql for the audit module first.';
    END IF;

    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'iam_audit_writer') THEN
        CREATE ROLE iam_audit_writer NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'iam_audit_reader') THEN
        CREATE ROLE iam_audit_reader NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;
    END IF;

    GRANT USAGE ON SCHEMA audit TO iam_audit_writer, iam_audit_reader;

    EXECUTE format('GRANT %I TO %I WITH INHERIT TRUE, SET FALSE', v_role, v_app);
END
$grant$;
