-- P0-013 slice 5 (DL-063a): a dedicated, short-lived login for the one-time access-control bootstrap on SQL Server.
-- Run as a server administrator, connected to the IAM database, after the Administration (`configuration`) migration
-- and grant-audit-role.sql (so iam_audit_writer exists). Revoke it with revoke-access-bootstrap.sql straight after the
-- bootstrap: SQL Server logins don't expire on their own. Runbook: docs/runbooks/ACCESS_CONTROL_BOOTSTRAP.md.
--
-- Inputs, bound as parameters by the caller and never concatenated into SQL text:
--   @BootstrapLogin     must start with iam_bootstrap (the tool refuses any other login)
--   @BootstrapPassword  at least 16 characters; generate it for this one run and don't store it
--
-- Grants only what the bootstrap needs: read the configuration schema, insert the default roles, their actions, the two
-- assignments, the incompatible pairs and version 1, and append its audit event. No UPDATE or DELETE anywhere.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF @BootstrapLogin IS NULL OR LEN(@BootstrapLogin) > 63 OR @BootstrapLogin COLLATE Latin1_General_100_BIN2 NOT LIKE 'iam[_]bootstrap%' OR @BootstrapLogin COLLATE Latin1_General_100_BIN2 LIKE '%[^a-z0-9_]%'
    THROW 50201, 'The bootstrap login name must start with iam_bootstrap and use a-z, 0-9 and _ only.', 1;
IF @BootstrapPassword IS NULL OR LEN(@BootstrapPassword) < 16
    THROW 50202, 'The bootstrap password must be at least 16 characters.', 1;
IF SCHEMA_ID(N'configuration') IS NULL OR OBJECT_ID(N'[configuration].[AccessModelVersion]') IS NULL
    THROW 50203, 'The configuration schema is not migrated yet; run the AccessControl migration first.', 1;
IF DATABASE_PRINCIPAL_ID(N'iam_audit_writer') IS NULL
    THROW 50204, 'Role iam_audit_writer does not exist; run grant-audit-role.sql first.', 1;

DECLARE @sql nvarchar(max);

IF SUSER_ID(@BootstrapLogin) IS NULL
BEGIN
    SET @sql = N'CREATE LOGIN ' + QUOTENAME(@BootstrapLogin) + N' WITH PASSWORD = ' + QUOTENAME(@BootstrapPassword, CHAR(39)) + N', CHECK_POLICY = ON;';
    EXEC sys.sp_executesql @sql;
END
ELSE
BEGIN
    SET @sql = N'ALTER LOGIN ' + QUOTENAME(@BootstrapLogin) + N' WITH PASSWORD = ' + QUOTENAME(@BootstrapPassword, CHAR(39)) + N'; ALTER LOGIN ' + QUOTENAME(@BootstrapLogin) + N' ENABLE;';
    EXEC sys.sp_executesql @sql;
END;
IF USER_ID(@BootstrapLogin) IS NULL
BEGIN
    SET @sql = N'CREATE USER ' + QUOTENAME(@BootstrapLogin) + N' FOR LOGIN ' + QUOTENAME(@BootstrapLogin) + N' WITH DEFAULT_SCHEMA = [configuration];';
    EXEC sys.sp_executesql @sql;
END;

SET @sql = N'GRANT SELECT ON SCHEMA::[configuration] TO ' + QUOTENAME(@BootstrapLogin) + N'; '
    + N'GRANT INSERT ON [configuration].[AccessRole] TO ' + QUOTENAME(@BootstrapLogin) + N'; '
    + N'GRANT INSERT ON [configuration].[AccessRoleAction] TO ' + QUOTENAME(@BootstrapLogin) + N'; '
    + N'GRANT INSERT ON [configuration].[AccessRoleAssignment] TO ' + QUOTENAME(@BootstrapLogin) + N'; '
    + N'GRANT INSERT ON [configuration].[AccessIncompatibleRolePair] TO ' + QUOTENAME(@BootstrapLogin) + N'; '
    + N'GRANT INSERT ON [configuration].[AccessModelVersion] TO ' + QUOTENAME(@BootstrapLogin) + N';';
EXEC sys.sp_executesql @sql;

IF IS_ROLEMEMBER(N'iam_audit_writer', @BootstrapLogin) = 0
BEGIN
    SET @sql = N'ALTER ROLE [iam_audit_writer] ADD MEMBER ' + QUOTENAME(@BootstrapLogin) + N';';
    EXEC sys.sp_executesql @sql;
END;
