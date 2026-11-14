using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Organization.Application.Workgroups;
using IAM.Modules.Organization.Contracts;
using IAM.Modules.Organization.Infrastructure.Persistence;
using IAM.Modules.Organization.Infrastructure.Workgroups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Organization.Infrastructure;

/// <summary>Registers IAM workgroups (DL-068). Called by <c>OrganizationModule</c>; the host then needs <c>ConnectionStrings:organization</c>.</summary>
public static class OrganizationInfrastructure
{
    public static IServiceCollection AddOrganizationInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<OrganizationDbContext>(OrganizationDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IWorkgroupAdministration, WorkgroupAdministrationService>();
        services.TryAddScoped<IWorkgroupDirectory, WorkgroupDirectory>();
        foreach (var action in WorkgroupAuditActions.All)
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }

        return services;
    }
}
