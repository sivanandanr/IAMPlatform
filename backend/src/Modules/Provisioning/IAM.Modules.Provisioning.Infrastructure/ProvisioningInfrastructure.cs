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
/// Registers provisioning (DL-093): the durable ledger and the consumer of authorisations, with the reviewed connector
/// allowlist. The host then needs <c>ConnectionStrings:provisioning</c>, and the hosts of the contracts the consumer uses
/// (the integrity check, request items, entitlements and accounts) registered with their own logins.
/// </summary>
public static class ProvisioningInfrastructure
{
    public static IServiceCollection AddProvisioningInfrastructure(this IServiceCollection services, IEnumerable<ConnectorBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(bindings);
        services.AddIamDbContext<ProvisioningDbContext>(ProvisioningDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new ConnectorBindings(bindings));
        services.TryAddScoped<IProvisioningLedger, ProvisioningLedgerStore>();
        services.TryAddScoped<AuthorizedApprovals>();
        foreach (var action in ProvisioningAuditActions.All)
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }

        return services;
    }
}
