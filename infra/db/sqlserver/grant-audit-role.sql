-- S1-07 audit role grant on SQL Server (ADR-0018). Run as a server administrator, connected to the IAM database,
-- after bootstrap-module.sql has created the 'audit' schema and the login's database user, and BEFORE the audit
-- migration (which grants to these roles and fails if they are missing). Idempotent.
--
-- Inputs, bound as parameters by the caller and never concatenated into SQL text:
--   @AppLogin   an existing module runtime login
--   @Role       'iam_audit_writer' (every module runtime login, including the audit login) or
--               'iam_audit_reader' (the audit login only)
--
-- Both roles are denied UPDATE, DELETE and ALTER on the whole audit schema, so membership can never change audit
-- rows; the deny also blocks TRUNCATE and IDENTITY_INSERT. The migration grants iam_audit_writer INSERT and
-- iam_audit_reader SELECT on audit.AuditEvent only (not on the migration history), and adds an INSTEAD OF UPDATE,
-- DELETE trigger.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF @AppLogin IS NULL OR LEN(@AppLogin) > 63 OR @AppLogin COLLATE Latin1_General_100_BIN2 NOT LIKE '[a-z]%' OR @AppLogin COLLATE Latin1_General_100_BIN2 LIKE '%[^a-z0-9_]%'
    THROW 50101, 'Invalid application login name.', 1;
IF @Role IS NULL OR @Role COLLATE Latin1_General_100_BIN2 NOT IN (N'iam_audit_writer', N'iam_audit_reader')
    THROW 50102, 'Role must be iam_audit_writer or iam_audit_reader.', 1;
IF USER_ID(@AppLogin) IS NULL
    THROW 50103, 'The application login has no user in this database; run bootstrap-module.sql first.', 1;
IF SCHEMA_ID(N'audit') IS NULL
    THROW 50104, 'Schema audit does not exist; run bootstrap-module.sql for the audit module first.', 1;

DECLARE @sql nvarchar(max);

IF DATABASE_PRINCIPAL_ID(N'iam_audit_writer') IS NULL
    CREATE ROLE [iam_audit_writer];
IF DATABASE_PRINCIPAL_ID(N'iam_audit_reader') IS NULL
    CREATE ROLE [iam_audit_reader];

DENY UPDATE, DELETE, ALTER ON SCHEMA::[audit] TO [iam_audit_writer];
DENY UPDATE, DELETE, ALTER ON SCHEMA::[audit] TO [iam_audit_reader];

IF IS_ROLEMEMBER(@Role, @AppLogin) = 0
BEGIN
    SET @sql = N'ALTER ROLE ' + QUOTENAME(@Role) + N' ADD MEMBER ' + QUOTENAME(@AppLogin) + N';';
    EXEC sys.sp_executesql @sql;
END;
