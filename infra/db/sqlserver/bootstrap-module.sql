-- S1-06 least-privilege bootstrap for one module schema on SQL Server (ADR-0017).
-- Run as a server administrator, connected to the IAM database, once per module. Idempotent.
--
-- Inputs are bound as parameters by the caller and never concatenated into SQL text:
--   @Schema            module schema, lower-case (e.g. identity)
--   @MigratorLogin     DDL principal that owns the module schemas (not sysadmin, not db_owner)
--   @MigratorPassword
--   @AppLogin          runtime principal for this module: SELECT/INSERT/UPDATE/DELETE on its own schema only
--   @AppPassword
-- Identifiers are validated against an allowlist pattern and quoted with QUOTENAME. CREATE LOGIN does not accept
-- parameters, so passwords are quoted with QUOTENAME(@password, char(39)) inside dynamic SQL.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF @Schema IS NULL OR LEN(@Schema) > 63 OR @Schema COLLATE Latin1_General_100_BIN2 NOT LIKE '[a-z]%' OR @Schema COLLATE Latin1_General_100_BIN2 LIKE '%[^a-z0-9_]%'
    THROW 50001, 'Invalid schema name.', 1;
IF @MigratorLogin IS NULL OR LEN(@MigratorLogin) > 63 OR @MigratorLogin COLLATE Latin1_General_100_BIN2 NOT LIKE '[a-z]%' OR @MigratorLogin COLLATE Latin1_General_100_BIN2 LIKE '%[^a-z0-9_]%'
    THROW 50002, 'Invalid migrator login name.', 1;
IF @AppLogin IS NULL OR LEN(@AppLogin) > 63 OR @AppLogin COLLATE Latin1_General_100_BIN2 NOT LIKE '[a-z]%' OR @AppLogin COLLATE Latin1_General_100_BIN2 LIKE '%[^a-z0-9_]%'
    THROW 50003, 'Invalid application login name.', 1;
IF @MigratorLogin = @AppLogin
    THROW 50004, 'The migrator and application logins must differ.', 1;
IF @MigratorPassword IS NULL OR @AppPassword IS NULL OR LEN(@MigratorPassword) < 16 OR LEN(@AppPassword) < 16
    THROW 50005, 'Passwords must be at least 16 characters.', 1;

DECLARE @sql nvarchar(max);

-- Logins and database users.
IF SUSER_ID(@MigratorLogin) IS NULL
BEGIN
    SET @sql = N'CREATE LOGIN ' + QUOTENAME(@MigratorLogin) + N' WITH PASSWORD = ' + QUOTENAME(@MigratorPassword, CHAR(39)) + N', CHECK_POLICY = ON;';
    EXEC sys.sp_executesql @sql;
END;
IF USER_ID(@MigratorLogin) IS NULL
BEGIN
    SET @sql = N'CREATE USER ' + QUOTENAME(@MigratorLogin) + N' FOR LOGIN ' + QUOTENAME(@MigratorLogin) + N';';
    EXEC sys.sp_executesql @sql;
END;
IF SUSER_ID(@AppLogin) IS NULL
BEGIN
    SET @sql = N'CREATE LOGIN ' + QUOTENAME(@AppLogin) + N' WITH PASSWORD = ' + QUOTENAME(@AppPassword, CHAR(39)) + N', CHECK_POLICY = ON;';
    EXEC sys.sp_executesql @sql;
END;
IF USER_ID(@AppLogin) IS NULL
BEGIN
    SET @sql = N'CREATE USER ' + QUOTENAME(@AppLogin) + N' FOR LOGIN ' + QUOTENAME(@AppLogin) + N';';
    EXEC sys.sp_executesql @sql;
END;

-- Migrator: may create objects, but only in schemas it owns. No server or database role membership.
SET @sql = N'GRANT CREATE TABLE, CREATE VIEW, CREATE FUNCTION, CREATE PROCEDURE, CREATE TYPE TO ' + QUOTENAME(@MigratorLogin) + N';';
EXEC sys.sp_executesql @sql;

-- Module schema owned by the migrator.
IF SCHEMA_ID(@Schema) IS NULL
BEGIN
    SET @sql = N'CREATE SCHEMA ' + QUOTENAME(@Schema) + N' AUTHORIZATION ' + QUOTENAME(@MigratorLogin) + N';';
    EXEC sys.sp_executesql @sql;
END
ELSE
BEGIN
    SET @sql = N'ALTER AUTHORIZATION ON SCHEMA::' + QUOTENAME(@Schema) + N' TO ' + QUOTENAME(@MigratorLogin) + N';';
    EXEC sys.sp_executesql @sql;
END;

-- Application: data access on its own schema only. No DDL, no other schemas.
SET @sql = N'GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::' + QUOTENAME(@Schema) + N' TO ' + QUOTENAME(@AppLogin) + N';';
EXEC sys.sp_executesql @sql;

-- Row versioning for READ COMMITTED (ADR-0017). Rolls back other connections' open transactions; run in a quiet window.
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_read_committed_snapshot_on = 1)
BEGIN
    SET @sql = N'ALTER DATABASE ' + QUOTENAME(DB_NAME()) + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;';
    EXEC sys.sp_executesql @sql;
END;
