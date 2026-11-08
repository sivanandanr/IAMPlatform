using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Identity.Application.Attributes;
using IAM.Modules.Identity.Contracts;
using IAM.Modules.Identity.Infrastructure.Attributes;
using IAM.Modules.Identity.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Identity.Infrastructure;

public static class IdentityInfrastructure
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<IdentityDbContext>(IdentityDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAttributeCatalog, AttributeCatalogReader>();
        services.TryAddScoped<IAttributeCatalogAdministration, AttributeCatalogAdministrationService>();
        foreach (var action in AttributeAuditActions.All)
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }

        return services;
    }
}
