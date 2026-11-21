using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Modules.Provisioning.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Provisioning.Infrastructure;

/// <summary>
/// Registers provisioning (DL-093, DL-094). The worker registers the consumer of authorisations with the reviewed connector
/// allowlist, and the runner with its settings; the API registers only the read of items for the IAM team. Either needs
/// <c>ConnectionStrings:provisioning</c>; the worker also needs the logins of the contracts it uses.
/// </summary>
public static class ProvisioningInfrastructure
{
    public static IServiceCollection AddProvisioningInfrastructure(this IServiceCollection services, IEnumerable<ConnectorBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(bindings);
        AddCore(services);
        services.TryAddSingleton(new ConnectorBindings(bindings));
        services.TryAddScoped<IProvisioningLedger, ProvisioningLedgerStore>();
        services.TryAddScoped<ProvisioningDecider>();
        services.TryAddScoped<AuthorizedApprovals>();
        return services;
    }

    /// <summary>The runner (DL-094): the host provides <see cref="IProvisioningTargets"/> and the Account contract.</summary>
    public static IServiceCollection AddProvisioningRunner(this IServiceCollection services, ProvisioningRunnerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        AddCore(services);
        services.TryAddSingleton(settings);
        services.TryAddScoped<ProvisioningDecider>();
        services.TryAddScoped<ProvisioningRunner>();
        return services;
    }

    /// <summary>The read of items for the IAM team (DL-094), behind <c>provisioning.items.read</c>.</summary>
    public static IServiceCollection AddProvisioningQueries(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddCore(services);
        services.TryAddScoped<IProvisioningItems, ProvisioningItemQueries>();
        return services;
    }

    private static void AddCore(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(ProvisioningCoreMarker)))
        {
            return;
        }

        services.AddSingleton<ProvisioningCoreMarker>();
        services.AddIamDbContext<ProvisioningDbContext>(ProvisioningDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        foreach (var action in ProvisioningAuditActions.All)
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }
    }

    private sealed class ProvisioningCoreMarker;
}
