-- S1-06 least-privilege bootstrap for one module schema on PostgreSQL (ADR-0017).
-- Run as a superuser or the database owner, connected to the IAM database, once per module. Idempotent.
--
-- Inputs are passed as session settings bound as parameters by the caller (never concatenated), for example
--   SELECT set_config('iam_bootstrap.schema', $1, false), set_config('iam_bootstrap.migrator_login', $2, false), ...
--   iam_bootstrap.schema             module schema, lower-case (e.g. identity)
--   iam_bootstrap.migrator_login     DDL role that owns the module schemas (NOSUPERUSER, NOCREATEROLE, NOCREATEDB)
--   iam_bootstrap.migrator_password
--   iam_bootstrap.app_login          runtime role for this module: SELECT/INSERT/UPDATE/DELETE on its own schema only
--   iam_bootstrap.app_password
-- Identifiers are validated against an allowlist pattern and quoted with format(%I); passwords use format(%L).
DO $bootstrap$
DECLARE
    v_schema       text := current_setting('iam_bootstrap.schema');
    v_migrator     text := current_setting('iam_bootstrap.migrator_login');
    v_migrator_pw  text := current_setting('iam_bootstrap.migrator_password');
    v_app          text := current_setting('iam_bootstrap.app_login');
    v_app_pw       text := current_setting('iam_bootstrap.app_password');
BEGIN
    IF v_schema !~ '^[a-z][a-z0-9_]{0,62}$' THEN RAISE EXCEPTION 'Invalid schema name.'; END IF;
    IF v_migrator !~ '^[a-z][a-z0-9_]{0,62}$' THEN RAISE EXCEPTION 'Invalid migrator login name.'; END IF;
    IF v_app !~ '^[a-z][a-z0-9_]{0,62}$' THEN RAISE EXCEPTION 'Invalid application login name.'; END IF;
    IF v_migrator = v_app THEN RAISE EXCEPTION 'The migrator and application logins must differ.'; END IF;
    IF coalesce(length(v_migrator_pw), 0) < 16 OR coalesce(length(v_app_pw), 0) < 16 THEN
        RAISE EXCEPTION 'Passwords must be at least 16 characters.';
    END IF;

    -- Roles: never superuser, never able to create roles or databases or bypass row security.
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = v_migrator) THEN
        EXECUTE format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L', v_migrator, v_migrator_pw);
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = v_app) THEN
        EXECUTE format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD %L', v_app, v_app_pw);
    END IF;

    -- Nobody gets anything by default.
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I, %I', current_database(), v_migrator, v_app);
    -- EF migrations issue CREATE SCHEMA IF NOT EXISTS, which needs CREATE on the database even when the schema exists.
    EXECUTE format('GRANT CREATE ON DATABASE %I TO %I', current_database(), v_migrator);

    -- Module schema owned by the migrator.
    EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I AUTHORIZATION %I', v_schema, v_migrator);
    EXECUTE format('ALTER SCHEMA %I OWNER TO %I', v_schema, v_migrator);

    -- Application: data access on its own schema only, including tables the migrator creates later.
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I', v_schema, v_app);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO %I', v_schema, v_app);
    EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO %I', v_schema, v_app);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', v_migrator, v_schema, v_app);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I GRANT USAGE, SELECT ON SEQUENCES TO %I', v_migrator, v_schema, v_app);
END
$bootstrap$;
