using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Catalog.Application.Applications;
using IAM.Modules.Catalog.Application.Entitlements;
using IAM.Modules.Catalog.Contracts;
using IAM.Modules.Catalog.Infrastructure.Applications;
using IAM.Modules.Catalog.Infrastructure.Entitlements;
using IAM.Modules.Catalog.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Catalog.Infrastructure;

/// <summary>Registers the application catalog (DL-066). Called by <c>CatalogModule</c>; the host then needs <c>ConnectionStrings:catalog</c>.</summary>
public static class CatalogInfrastructure
{
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<CatalogDbContext>(CatalogDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IApplicationCatalogAdministration, ApplicationCatalogAdministrationService>();
        services.TryAddScoped<IApplicationApproverDirectory, ApplicationApproverDirectory>();
        services.TryAddScoped<IEntitlementCatalogAdministration, EntitlementCatalogAdministrationService>();
        services.TryAddScoped<IEntitlementDirectory, EntitlementDirectory>();
        foreach (var action in CatalogAuditActions.All.Concat(EntitlementAuditActions.All))
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }

        return services;
    }
}
