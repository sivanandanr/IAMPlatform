using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Administration.Application.Access;
using IAM.Modules.Administration.Infrastructure.Access;
using IAM.Modules.Administration.Infrastructure.Persistence;
using IAM.Modules.Audit.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Administration.Infrastructure;

/// <summary>
/// Registers stored access control. Not yet called by the API host: registering a module makes the host require its
/// connection string, so this is wired in with the endpoints (stage 2, slice 3).
/// </summary>
public static class AdministrationInfrastructure
{
    public static IServiceCollection AddAdministrationInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<AdministrationDbContext>(AdministrationDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAccessAdministration, AccessAdministrationService>();
        foreach (var action in AccessAuditActions.All)
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }

        return services;
    }
}
