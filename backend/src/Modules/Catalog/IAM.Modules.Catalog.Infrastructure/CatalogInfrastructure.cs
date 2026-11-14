using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Catalog.Application.Applications;
using IAM.Modules.Catalog.Infrastructure.Applications;
using IAM.Modules.Catalog.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Catalog.Infrastructure;

/// <summary>
/// Registers the application catalog (DL-066). Not yet called by the API host: registering a module makes the host require
/// its connection string, so this is wired in with the endpoints (stage 3, slice 2).
/// </summary>
public static class CatalogInfrastructure
{
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<CatalogDbContext>(CatalogDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IApplicationCatalogAdministration, ApplicationCatalogAdministrationService>();
        foreach (var action in CatalogAuditActions.All)
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }

        return services;
    }
}
