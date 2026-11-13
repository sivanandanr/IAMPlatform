using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using DotNet.Testcontainers.Containers;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Administration.Infrastructure.Persistence;
using IAM.Modules.Audit.Infrastructure.Persistence;
using IAM.Modules.Identity.Infrastructure.Persistence;
using IAM.Persistence.Tests.Probe;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace IAM.Persistence.Tests.Parity;

[CollectionDefinition(Name)]
public sealed class ParityDatabaseGroup : ICollectionFixture<ParityDatabase>
{
    public const string Name = "Database parity";
}

/// <summary>
/// One throwaway database engine per test run (<c>IAM_PARITY_PROVIDER</c>), set up exactly as a deployment would be:
/// bootstrap script as administrator, product migrations as the migrator login, data access as per-module app logins.
/// Images are the same pinned digests as compose.yaml (ADR-0023).
/// </summary>
public sealed class ParityDatabase : IAsyncLifetime
{
    public const string ProviderVariable = "IAM_PARITY_PROVIDER";
    public const string MigratorLogin = "iam_migrator";
    public const string IdentityAppLogin = "iam_app_identity";
    public const string ProbeAppLogin = "iam_app_parity_probe";
    public const string AuditAppLogin = "iam_app_audit";
    public const string AdministrationAppLogin = "iam_app_administration";

    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718";
    private const string PostgreSqlImage = "postgres:17.11-alpine3.24@sha256:3540c0362c809df084ffac0ae69fe794515f9de92692f02178bcdce0ef0302ac";
    private const string DatabaseName = "iam";

    private readonly string _migratorPassword = NewPassword();
    private readonly string _identityAppPassword = NewPassword();
    private readonly string _probeAppPassword = NewPassword();
    private readonly string _auditAppPassword = NewPassword();
    private readonly string _administrationAppPassword = NewPassword();
    private IDatabaseContainer? _container;
    private string _adminConnectionString = string.Empty;

    public DatabaseProvider Provider { get; private set; }

    public IReadOnlyList<string> FirstRunApplied { get; private set; } = [];

    public IReadOnlyList<string> SecondRunPending { get; private set; } = [];

    public string MigratorConnectionString => WithLogin(MigratorLogin, _migratorPassword);

    public string IdentityAppConnectionString => WithLogin(IdentityAppLogin, _identityAppPassword);

    public string ProbeAppConnectionString => WithLogin(ProbeAppLogin, _probeAppPassword);

    public string AuditAppConnectionString => WithLogin(AuditAppLogin, _auditAppPassword);

    public string AdministrationAppConnectionString => WithLogin(AdministrationAppLogin, _administrationAppPassword);

    public IReadOnlyList<string> AdministrationFirstRunApplied { get; private set; } = [];

    public IReadOnlyList<string> AdministrationSecondRunPending { get; private set; } = [];

    public IReadOnlyList<string> AuditFirstRunApplied { get; private set; } = [];

    public IReadOnlyList<string> AuditSecondRunPending { get; private set; } = [];

    public string AdminConnectionString => _adminConnectionString;

    public async ValueTask InitializeAsync()
    {
        Provider = DatabaseProviderSetting.Parse(Environment.GetEnvironmentVariable(ProviderVariable) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{ProviderVariable} is not set. Parity tests run against one real engine per run: set it to 'SqlServer' or 'PostgreSql'."));

        var ct = TestContext.Current.CancellationToken;
        if (Provider == DatabaseProvider.SqlServer)
        {
            // Nobody accepts the SQL Server licence on the developer's behalf (ADR-0023): the runner must set it.
            if (Environment.GetEnvironmentVariable("MSSQL_ACCEPT_EULA") != "Y")
            {
                throw new InvalidOperationException(
                    "SQL Server parity tests need MSSQL_ACCEPT_EULA=Y, set by whoever accepts the SQL Server Developer licence for this run.");
            }

            var container = new MsSqlBuilder(SqlServerImage).WithEnvironment("ACCEPT_EULA", "Y").Build();
            _container = container;
            await container.StartAsync(ct);
            await ExecuteAsync(container.GetConnectionString(), "CREATE DATABASE [iam];", ct);
            _adminConnectionString = new SqlConnectionStringBuilder(container.GetConnectionString()) { InitialCatalog = DatabaseName }.ConnectionString;
        }
        else
        {
            var container = new PostgreSqlBuilder(PostgreSqlImage)
                .WithDatabase(DatabaseName)
                .WithUsername("iam_admin")
                .WithPassword(NewPassword())
                .Build();
            _container = container;
            await container.StartAsync(ct);
            _adminConnectionString = container.GetConnectionString();
        }

        await BootstrapAsync(IdentityDbContext.SchemaName, IdentityAppLogin, _identityAppPassword, ct);
        await BootstrapAsync(ProbeDbContext.SchemaName, ProbeAppLogin, _probeAppPassword, ct);
        await BootstrapAsync(AuditDbContext.SchemaName, AuditAppLogin, _auditAppPassword, ct);
        await BootstrapAsync(AdministrationDbContext.SchemaName, AdministrationAppLogin, _administrationAppPassword, ct);

        // Every module runtime login may append audit events in its own transaction; only the audit login may read
        // them (ADR-0018). Roles must exist before the audit migration grants to them.
        foreach (var login in new[] { IdentityAppLogin, ProbeAppLogin, AuditAppLogin, AdministrationAppLogin })
        {
            await GrantAuditRoleAsync(login, AuditDbContext.WriterRole, ct);
        }

        await GrantAuditRoleAsync(AuditAppLogin, AuditDbContext.ReaderRole, ct);

        // Product migrations, as the migrator, twice: the second run must find nothing to do.
        await using (var identity = TestSupport.CreateIdentity(Provider, MigratorConnectionString))
        {
            var pending = (await identity.Database.GetPendingMigrationsAsync(ct)).ToList();
            await identity.Database.MigrateAsync(ct);
            FirstRunApplied = pending;
        }

        await using (var identity = TestSupport.CreateIdentity(Provider, MigratorConnectionString))
        {
            SecondRunPending = (await identity.Database.GetPendingMigrationsAsync(ct)).ToList();
            await identity.Database.MigrateAsync(ct);
        }

        await using (var audit = TestSupport.CreateAudit(Provider, MigratorConnectionString))
        {
            var pending = (await audit.Database.GetPendingMigrationsAsync(ct)).ToList();
            await audit.Database.MigrateAsync(ct);
            AuditFirstRunApplied = pending;
        }

        await using (var audit = TestSupport.CreateAudit(Provider, MigratorConnectionString))
        {
            AuditSecondRunPending = (await audit.Database.GetPendingMigrationsAsync(ct)).ToList();
            await audit.Database.MigrateAsync(ct);
        }

        await using (var administration = TestSupport.CreateAdministration(Provider, MigratorConnectionString))
        {
            var pending = (await administration.Database.GetPendingMigrationsAsync(ct)).ToList();
            await administration.Database.MigrateAsync(ct);
            AdministrationFirstRunApplied = pending;
        }

        await using (var administration = TestSupport.CreateAdministration(Provider, MigratorConnectionString))
        {
            AdministrationSecondRunPending = (await administration.Database.GetPendingMigrationsAsync(ct)).ToList();
            await administration.Database.MigrateAsync(ct);
        }

        // Probe tables, created by the migrator from the model with the provider's own DDL generator.
        await using (var probe = TestSupport.CreateProbe(Provider, MigratorConnectionString))
        {
            await probe.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public ProbeDbContext CreateProbe(string? actor = "parity-tester", TimeProvider? clock = null) =>
        TestSupport.CreateProbe(Provider, ProbeAppConnectionString, new AuditingSaveChangesInterceptor(clock ?? TimeProvider.System, new FixedActor(actor)));

    public DbConnection OpenConnection(string connectionString)
    {
        DbConnection connection = Provider == DatabaseProvider.SqlServer
            ? new SqlConnection(connectionString)
            : new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Runs constant SQL text (no user input) on a fresh connection.</summary>
    [SuppressMessage("Security", "CA2100", Justification = "Test helper: SQL text is a constant in test code; no user input.")]
    public async Task ExecuteAsync(string connectionString, string constantSql, CancellationToken ct)
    {
        await using var connection = OpenConnection(connectionString);
        await using var command = connection.CreateCommand();
        command.CommandText = constantSql;
        await command.ExecuteNonQueryAsync(ct);
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test helper: SQL text is a constant in test code; no user input.")]
    public async Task<object?> ScalarAsync(string connectionString, string constantSql, CancellationToken ct)
    {
        await using var connection = OpenConnection(connectionString);
        await using var command = connection.CreateCommand();
        command.CommandText = constantSql;
        return await command.ExecuteScalarAsync(ct);
    }

    [SuppressMessage("Security", "CA2100", Justification = "Executes the reviewed bootstrap script from infra/db; every input is a bound parameter.")]
    private async Task BootstrapAsync(string schema, string appLogin, string appPassword, CancellationToken ct)
    {
        var dialect = Provider == DatabaseProvider.SqlServer ? "sqlserver" : "postgresql";
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", dialect, "bootstrap-module.sql"), ct);
        await using var connection = OpenConnection(_adminConnectionString);

        if (Provider == DatabaseProvider.SqlServer)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = script;
            AddParameter(command, "@Schema", schema);
            AddParameter(command, "@MigratorLogin", MigratorLogin);
            AddParameter(command, "@MigratorPassword", _migratorPassword);
            AddParameter(command, "@AppLogin", appLogin);
            AddParameter(command, "@AppPassword", appPassword);
            await command.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using (var settings = connection.CreateCommand())
            {
                settings.CommandText =
                    "SELECT set_config('iam_bootstrap.schema', @schema, false), " +
                    "set_config('iam_bootstrap.migrator_login', @migrator, false), " +
                    "set_config('iam_bootstrap.migrator_password', @migratorPassword, false), " +
                    "set_config('iam_bootstrap.app_login', @app, false), " +
                    "set_config('iam_bootstrap.app_password', @appPassword, false)";
                AddParameter(settings, "@schema", schema);
                AddParameter(settings, "@migrator", MigratorLogin);
                AddParameter(settings, "@migratorPassword", _migratorPassword);
                AddParameter(settings, "@app", appLogin);
                AddParameter(settings, "@appPassword", appPassword);
                await settings.ExecuteNonQueryAsync(ct);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = script;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    [SuppressMessage("Security", "CA2100", Justification = "Executes the reviewed grant script from infra/db; the inputs are bound parameters.")]
    private async Task GrantAuditRoleAsync(string appLogin, string role, CancellationToken ct)
    {
        var dialect = Provider == DatabaseProvider.SqlServer ? "sqlserver" : "postgresql";
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", dialect, "grant-audit-role.sql"), ct);
        await using var connection = OpenConnection(_adminConnectionString);
        if (Provider == DatabaseProvider.SqlServer)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = script;
            AddParameter(command, "@AppLogin", appLogin);
            AddParameter(command, "@Role", role);
            await command.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using (var settings = connection.CreateCommand())
            {
                settings.CommandText = "SELECT set_config('iam_bootstrap.app_login', @app, false), set_config('iam_bootstrap.audit_role', @role, false)";
                AddParameter(settings, "@app", appLogin);
                AddParameter(settings, "@role", role);
                await settings.ExecuteNonQueryAsync(ct);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = script;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>The connection string for <paramref name="login"/> on this database (the bootstrap login tests, slice 5).</summary>
    public string ConnectionStringFor(string login, string password) => WithLogin(login, password);

    /// <summary>Runs the reviewed grant-access-bootstrap.sql as administrator (DL-063a).</summary>
    public Task GrantAccessBootstrapLoginAsync(string login, string password, CancellationToken ct) =>
        RunAdminScriptAsync("grant-access-bootstrap.sql", [("@BootstrapLogin", "iam_bootstrap.login", login), ("@BootstrapPassword", "iam_bootstrap.password", password)], ct);

    /// <summary>Runs the reviewed revoke-access-bootstrap.sql as administrator (DL-063a).</summary>
    public Task RevokeAccessBootstrapLoginAsync(string login, CancellationToken ct) =>
        RunAdminScriptAsync("revoke-access-bootstrap.sql", [("@BootstrapLogin", "iam_bootstrap.login", login)], ct);

    [SuppressMessage("Security", "CA2100", Justification = "Executes a reviewed script from infra/db; every input is a bound parameter.")]
    private async Task RunAdminScriptAsync(string file, (string SqlServerName, string PostgreSqlSetting, string Value)[] inputs, CancellationToken ct)
    {
        var dialect = Provider == DatabaseProvider.SqlServer ? "sqlserver" : "postgresql";
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", dialect, file), ct);
        await using var connection = OpenConnection(_adminConnectionString);
        if (Provider == DatabaseProvider.SqlServer)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = script;
            foreach (var (name, _, value) in inputs)
            {
                AddParameter(command, name, value);
            }

            await command.ExecuteNonQueryAsync(ct);
            return;
        }

        await using (var settings = connection.CreateCommand())
        {
            settings.CommandText = "SELECT " + string.Join(", ", inputs.Select((input, i) => $"set_config('{input.PostgreSqlSetting}', @p{i}, false)"));
            for (var i = 0; i < inputs.Length; i++)
            {
                AddParameter(settings, $"@p{i}", inputs[i].Value);
            }

            await settings.ExecuteNonQueryAsync(ct);
        }

        await using var run = connection.CreateCommand();
        run.CommandText = script;
        await run.ExecuteNonQueryAsync(ct);
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private string WithLogin(string login, string password) => Provider == DatabaseProvider.SqlServer
        ? new SqlConnectionStringBuilder(_adminConnectionString) { UserID = login, Password = password, Pooling = false }.ConnectionString
        : new NpgsqlConnectionStringBuilder(_adminConnectionString) { Username = login, Password = password, Pooling = false }.ConnectionString;

    /// <summary>Random per-run password that satisfies SQL Server's policy; never logged or persisted.</summary>
    internal static string NewPassword() =>
        "Aa1-" + RandomNumberGenerator.GetString("abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789", 32);
}
