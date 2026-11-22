using IAM.BuildingBlocks.Messaging.RabbitMq;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Connectors.ActiveDirectory.Mock;
using IAM.Connectors.Sdk;
using IAM.Modules.Account.Infrastructure;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Catalog.Infrastructure;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Modules.Request.Infrastructure;
using IAM.Modules.Workflow.Infrastructure;
using IAM.Persistence.PostgreSql;
using IAM.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IAM.Workers.Provisioning;

/// <summary>
/// Builds the provisioning worker (DL-093): one database provider (<c>IAM_DB_PROVIDER</c>), a connection string per schema it
/// uses (<c>provisioning</c> to write; <c>workflow</c>, <c>audit</c>, <c>request</c>, <c>catalog</c> and <c>account</c> to
/// read, each with that module's own login, and <c>audit</c> the audit login that may only read audit rows), the broker
/// (<c>Messaging:RabbitMq</c>, the provisioning module's own broker user), the consumer settings (<c>Provisioning:Consumer</c>)
/// and the reviewed connector allowlist (<c>Provisioning:Connectors</c>), all checked before it runs. It writes as the fixed
/// system actor <c>system:provisioning</c>, and every permission check says no. Since DL-094 it also runs planned items on
/// their targets (<c>Provisioning:Runner</c>) and records the accounts it created (the account login). The only target is
/// the in-memory mock AD connector (<c>Provisioning:Targets:mock-ad:BaseDn</c>): never production-ready (rule 10), and it
/// starts empty, so its containers and groups must exist before anything can be created in it.
/// </summary>
public static class ProvisioningWorkerHost
{
    public const string Actor = "system:provisioning";

    public const string ConsumerSection = "Provisioning:Consumer";

    public const string ConnectorsSection = "Provisioning:Connectors";

    public const string MockBaseDnSetting = "Provisioning:Targets:mock-ad:BaseDn";

    /// <summary>The most applications the allowlist may name.</summary>
    public const int MaxBindings = 500;

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

        // What the consumer reads, each with its own module's login; the audit trail with the audit login (R-037).
        services.AddAuditInfrastructure();
        services.AddAuditTrail();
        services.AddApprovalIntegrity();
        services.AddRequestItemSource();
        services.AddCatalogInfrastructure();
        services.AddAccountInfrastructure();
        services.AddProvisioningInfrastructure(Bindings(configuration));
        services.AddSingleton<IOperationLedger, DurableOperationLedger>();

        // The runner (DL-094) and its one target: the mock AD connector, with the durable ledger.
        var runner = configuration.GetSection(ProvisioningRunnerSettings.Section);
        services.AddProvisioningRunner(new ProvisioningRunnerSettings(
            runner.GetValue<int?>("IntervalSeconds"), runner.GetValue<int?>("BatchSize"), runner.GetValue<int?>("LeaseSeconds"),
            runner.GetValue<int?>("MaxAttempts"), runner.GetValue<int?>("RetryDelaySeconds"), runner.GetValue<int?>("StepTimeoutSeconds"),
            runner.GetValue<int?>("UndoMaxAttempts"), runner.GetValue<int?>("MaxPostponements")));
        var baseDn = configuration[MockBaseDnSetting] ?? MockActiveDirectoryConnector.DefaultBaseDn;
        services.AddSingleton(sp => new MockActiveDirectoryConnector(ConnectorSettings.Default, sp.GetRequiredService<IOperationLedger>(), baseDn, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProvisioningTargets>(sp => new ConnectorTargets(new Dictionary<string, IProvisioningTarget>(StringComparer.Ordinal)
        {
            ["mock-ad"] = new ConnectorTarget(sp.GetRequiredService<MockActiveDirectoryConnector>()),
        }));
        services.AddHostedService<ProvisioningRunnerWorker>();

        services.AddSingleton(RabbitMqSettings.From(configuration));
        services.AddSingleton(ConsumerSettings.From(configuration, ConsumerSection));
        services.AddHostedService<RabbitMqConsumer<AuthorizedApprovals>>();

        var host = builder.Build();

        // Every schema the worker reads or writes must have its own connection string, or it doesn't start (ADR-0017).
        var connectionStrings = host.Services.GetRequiredService<IIamConnectionStrings>();
        foreach (var schema in host.Services.GetService<IamPersistenceRegistry>()?.Schemas ?? [])
        {
            connectionStrings.ForSchema(schema);
        }

        return host;
    }

    /// <summary>
    /// The reviewed allowlist (DL-093, DL-098): <c>Provisioning:Connectors:N:ApplicationId</c>, <c>ConnectorKey</c>,
    /// <c>AccountContainer</c> and <c>GroupContainer</c>. A malformed or repeated entry stops the worker; an empty list means every application is manual.
    /// </summary>
    public static IReadOnlyList<ConnectorBinding> Bindings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var entries = configuration.GetSection(ConnectorsSection).GetChildren().ToList();
        if (entries.Count > MaxBindings)
        {
            throw new InvalidOperationException($"{ConnectorsSection} names more than {MaxBindings} applications.");
        }

        var bindings = new List<ConnectorBinding>();
        foreach (var entry in entries)
        {
            if (!Guid.TryParseExact(entry["ApplicationId"], "D", out var applicationId))
            {
                throw new InvalidOperationException($"{ConnectorsSection}:{entry.Key}:ApplicationId must be a GUID.");
            }

            try
            {
                bindings.Add(ConnectorBinding.Create(applicationId, entry["ConnectorKey"], entry["AccountContainer"], entry["GroupContainer"]));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"{ConnectorsSection}:{entry.Key} is not a valid binding ({ex.ParamName}).", ex);
            }
        }

        return bindings.DistinctBy(b => b.ApplicationId).Count() == bindings.Count
            ? bindings
            : throw new InvalidOperationException($"{ConnectorsSection} names an application more than once.");
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
