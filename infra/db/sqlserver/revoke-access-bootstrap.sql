-- P0-013 slice 5 (DL-063a): removes the bootstrap login straight after the one-time bootstrap. Run as a server
-- administrator, connected to the IAM database. Idempotent. Fails if the login still has open sessions: close the tool
-- first.
--
-- Input, bound as a parameter by the caller:
--   @BootstrapLogin  the login grant-access-bootstrap.sql created (starts with iam_bootstrap)
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF @BootstrapLogin IS NULL OR LEN(@BootstrapLogin) > 63 OR @BootstrapLogin COLLATE Latin1_General_100_BIN2 NOT LIKE 'iam[_]bootstrap%' OR @BootstrapLogin COLLATE Latin1_General_100_BIN2 LIKE '%[^a-z0-9_]%'
    THROW 50211, 'The bootstrap login name must start with iam_bootstrap and use a-z, 0-9 and _ only.', 1;

DECLARE @sql nvarchar(max);

IF USER_ID(@BootstrapLogin) IS NOT NULL
BEGIN
    IF IS_ROLEMEMBER(N'iam_audit_writer', @BootstrapLogin) = 1
    BEGIN
        SET @sql = N'ALTER ROLE [iam_audit_writer] DROP MEMBER ' + QUOTENAME(@BootstrapLogin) + N';';
        EXEC sys.sp_executesql @sql;
    END;
    SET @sql = N'DROP USER ' + QUOTENAME(@BootstrapLogin) + N';';
    EXEC sys.sp_executesql @sql;
END;
IF SUSER_ID(@BootstrapLogin) IS NOT NULL
BEGIN
    SET @sql = N'DROP LOGIN ' + QUOTENAME(@BootstrapLogin) + N';';
    EXEC sys.sp_executesql @sql;
END;
