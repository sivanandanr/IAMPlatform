using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Catalog.Infrastructure;
using IAM.Modules.Organization.Infrastructure;
using IAM.Modules.Workflow.Infrastructure;
using IAM.Persistence.PostgreSql;
using IAM.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IAM.Workers.Workflow;

/// <summary>
/// Builds the workflow worker (DL-074): one database provider (<c>IAM_DB_PROVIDER</c>), a connection string per schema it
/// uses (<c>workflow</c>, <c>catalog</c>, <c>organization</c>), and the timeout settings, all checked before it runs. It
/// writes as the fixed system actor <c>system:workflow-timeouts</c>, and every permission check says no: applying timeouts
/// needs none, and nothing in the worker can escalate or cancel.
/// </summary>
public static class WorkflowWorkerHost
{
    public const string Actor = "system:workflow-timeouts";

    public static IHost Build(string[] args, IEnumerable<KeyValuePair<string, string?>>? settings = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        var configuration = builder.Configuration;
        var services = builder.Services;
        var provider = DatabaseProviderSetting.Parse(configuration[DatabaseProviderSetting.Key]);
        services.AddSingleton<IIamDatabaseProvider>(provider switch
        {
            DatabaseProvider.SqlServer => new SqlServerDatabaseProvider(),
            DatabaseProvider.PostgreSql => new PostgreSqlDatabaseProvider(),
            _ => throw new InvalidOperationException("Unsupported database provider."),
        });
        services.AddSingleton<IIamConnectionStrings>(new ConfigurationConnectionStrings(configuration));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditActorAccessor>(new SystemActor(Actor));
        services.TryAddSingleton<IPermissionEvaluator, NoPermissions>();

        services.AddAuditInfrastructure();
        services.AddCatalogInfrastructure();
        services.AddOrganizationInfrastructure();
        services.AddWorkflowInfrastructure();

        var section = configuration.GetSection(ApprovalTimeoutSettings.Section);
        services.AddSingleton(new ApprovalTimeoutSettings(section.GetValue<int?>("IntervalSeconds"), section.GetValue<int?>("BatchSize")));
        services.AddSingleton<ApprovalTimeoutSweeper>();
        services.AddHostedService<ApprovalTimeoutWorker>();

        var host = builder.Build();

        // Every schema the worker reads or writes must have its own connection string, or it doesn't start (ADR-0017).
        var connectionStrings = host.Services.GetRequiredService<IIamConnectionStrings>();
        foreach (var schema in host.Services.GetService<IamPersistenceRegistry>()?.Schemas ?? [])
        {
            connectionStrings.ForSchema(schema);
        }

        return host;
    }

    private sealed class ConfigurationConnectionStrings(IConfiguration configuration) : IIamConnectionStrings
    {
        public string ForSchema(string schema)
        {
            var value = configuration.GetConnectionString(schema);
            return string.IsNullOrWhiteSpace(value)
                ? throw new InvalidOperationException(
                    $"ConnectionStrings:{schema} is not configured. Each module schema needs its own least-privilege login (ADR-0017).")
                : value;
        }
    }

    private sealed class SystemActor(string actor) : IAuditActorAccessor
    {
        public string? CurrentActor => actor;
    }

    /// <summary>The worker acts for nobody: every permission check fails closed.</summary>
    private sealed class NoPermissions : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
