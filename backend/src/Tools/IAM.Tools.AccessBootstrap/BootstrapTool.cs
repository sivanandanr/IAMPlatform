using System.Data.Common;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Administration.Application.Access;
using IAM.Modules.Administration.Endpoints;
using IAM.Modules.Audit.Endpoints;
using IAM.Persistence.PostgreSql;
using IAM.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Bootstrap = IAM.Modules.Administration.Endpoints.AccessBootstrap;

namespace IAM.Tools.AccessBootstrap;

/// <summary>Parsed command line. Secrets never come from arguments: the connection string is read from the environment.</summary>
internal sealed record BootstrapArguments(string Administrator, string Manager, string Operator, string Approval)
{
    public const string Usage =
        "Usage: IAM.Tools.AccessBootstrap --administrator <sub> --manager <sub> --operator <your-name> --approval <ticket> --confirm\n" +
        "Environment: IAM_DB_PROVIDER (SqlServer|PostgreSql), ConnectionStrings__bootstrap (the short-lived iam_bootstrap login from\n" +
        "grant-access-bootstrap.sql; never the API's own login).";

    private static readonly string[] Options = ["--administrator", "--manager", "--operator", "--approval"];

    /// <summary>Exact options, each once, values that aren't options; <c>--confirm</c> required; operator and approval are short plain codes.</summary>
    public static BootstrapArguments? Parse(IReadOnlyList<string> args, out string? error)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var confirmed = false;
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name == "--confirm")
            {
                confirmed = true;
                continue;
            }

            if (!Options.Contains(name, StringComparer.Ordinal) || i + 1 >= args.Count || values.ContainsKey(name)
                || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected, repeated or empty argument: {(name.StartsWith("--", StringComparison.Ordinal) ? name : "(value)")}.";
                return null;
            }

            values[name] = args[++i];
        }

        if (Options.Any(o => !values.ContainsKey(o)))
        {
            error = "--administrator, --manager, --operator and --approval are required.";
            return null;
        }

        if (!confirmed)
        {
            error = "Add --confirm: this names the first two people who can change access, once.";
            return null;
        }

        if (!IsCode(values["--operator"], lowerOnly: true))
        {
            error = "--operator must be 1-64 characters of a-z, 0-9, '.', '-' or '_'.";
            return null;
        }

        if (!IsCode(values["--approval"], lowerOnly: false) || values["--approval"].Length < 3)
        {
            error = "--approval must be the approval or ticket reference: 3-64 characters of letters, digits, '.', '-', '_', ':' or '/'.";
            return null;
        }

        error = null;
        return new BootstrapArguments(values["--administrator"], values["--manager"], values["--operator"], values["--approval"]);
    }

    private static bool IsCode(string value, bool lowerOnly) =>
        value.Length is > 0 and <= 64
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_'
            || (!lowerOnly && (c is >= 'A' and <= 'Z' or ':' or '/')));
}

/// <summary>
/// Runs the bootstrap (DL-059e, DL-062c, DL-063a). Exit codes: 0 done, 2 refused (already set up, same person, invalid
/// subject), 1 anything else (arguments, configuration, the wrong login, database). Output names a refusal code or an
/// exception type only, never a message that could hold a host, login or stored value.
/// </summary>
internal static class BootstrapTool
{
    public const int Done = 0;
    public const int Failed = 1;
    public const int Refused = 2;

    /// <summary>The login must be the dedicated one from grant-access-bootstrap.sql, never the API's own.</summary>
    public const string RequiredLoginPrefix = "iam_bootstrap";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, IConfiguration configuration, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            return await RunCoreAsync(args, configuration, output, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await output.WriteLineAsync("Cancelled. If the bootstrap had started, check the audit log before running it again.").ConfigureAwait(false);
            return Failed;
        }
        catch (Exception ex)
        {
            // The type only: messages can name the database host, the login or stored values.
            await output.WriteLineAsync($"Failed: {ex.GetType().Name}. Nothing is known to have changed; check the database and the audit log.").ConfigureAwait(false);
            return Failed;
        }
    }

    private static async Task<int> RunCoreAsync(IReadOnlyList<string> args, IConfiguration configuration, TextWriter output, CancellationToken cancellationToken)
    {
        var parsed = BootstrapArguments.Parse(args, out var error);
        if (parsed is null)
        {
            await output.WriteLineAsync(error).ConfigureAwait(false);
            await output.WriteLineAsync(BootstrapArguments.Usage).ConfigureAwait(false);
            return Failed;
        }

        IIamDatabaseProvider provider;
        try
        {
            provider = DatabaseProviderSetting.Parse(configuration[DatabaseProviderSetting.Key]) switch
            {
                DatabaseProvider.SqlServer => new SqlServerDatabaseProvider(),
                DatabaseProvider.PostgreSql => new PostgreSqlDatabaseProvider(),
                _ => throw new InvalidOperationException("Unsupported database provider."),
            };
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return Failed;
        }

        var connectionString = configuration.GetConnectionString("bootstrap");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await output.WriteLineAsync("ConnectionStrings__bootstrap is not set (the short-lived iam_bootstrap login, ACCESS_CONTROL_BOOTSTRAP.md).").ConfigureAwait(false);
            return Failed;
        }

        if (!LoginOf(connectionString).StartsWith(RequiredLoginPrefix, StringComparison.Ordinal))
        {
            await output.WriteLineAsync($"Refusing to run with this login: use the dedicated {RequiredLoginPrefix} login from grant-access-bootstrap.sql, never the API's own.").ConfigureAwait(false);
            return Failed;
        }

        var services = new ServiceCollection();
        services.AddSingleton(provider);
        services.AddSingleton<IIamConnectionStrings>(new SingleSchemaConnectionStrings("configuration", connectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditActorAccessor>(new OperatorActor($"system:bootstrap:{parsed.Operator}"));
        services.AddLogging();
        services.AddAuditModule();
        services.AddAdministrationModule();
        await using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var evidence = new BootstrapEvidence(parsed.Approval, Environment.UserName, Environment.MachineName);
        var result = await Bootstrap.RunAsync(root, parsed.Administrator, parsed.Manager, evidence, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await output.WriteLineAsync("Done: the first IAM Administrator and IAM Manager are set, and this is audited. It can't be run again. Now revoke the bootstrap login.").ConfigureAwait(false);
            return Done;
        }

        await output.WriteLineAsync($"Refused: {result.RefusalCode ?? result.Failure.ToString()}. Nothing was changed.").ConfigureAwait(false);
        return result.RefusalCode is null ? Failed : Refused;
    }

    /// <summary>The login named in the connection string (SQL Server "User ID", PostgreSQL "Username"), or empty.</summary>
    internal static string LoginOf(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (var key in new[] { "User ID", "UID", "User", "Username", "User Name" })
        {
            if (builder.TryGetValue(key, out var value) && value is string login && login.Length > 0)
            {
                return login;
            }
        }

        return string.Empty;
    }

    private sealed class SingleSchemaConnectionStrings(string schema, string connectionString) : IIamConnectionStrings
    {
        public string ForSchema(string requested) => string.Equals(requested, schema, StringComparison.Ordinal)
            ? connectionString
            : throw new InvalidOperationException($"The bootstrap tool only connects to schema '{schema}'.");
    }

    private sealed class OperatorActor(string actor) : IAuditActorAccessor
    {
        public string? CurrentActor { get; } = actor;
    }
}
