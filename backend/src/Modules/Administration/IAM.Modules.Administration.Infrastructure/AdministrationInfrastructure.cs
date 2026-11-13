using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Application.Access;
using IAM.Modules.Administration.Infrastructure.Access;
using IAM.Modules.Administration.Infrastructure.Persistence;
using IAM.Modules.Audit.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Administration.Infrastructure;

/// <summary>
/// Registers stored access control and the permission evaluator every endpoint check uses (P0-013 slices 2-3). The API
/// host then needs <c>ConnectionStrings:configuration</c> (ADR-0017).
/// </summary>
public static class AdministrationInfrastructure
{
    public static IServiceCollection AddAdministrationInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<AdministrationDbContext>(AdministrationDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAccessAdministration, AccessAdministrationService>();
        services.TryAddScoped<IPermissionEvaluator, AccessPermissionEvaluator>();
        services.TryAddSingleton<AccessIntegrityAuditThrottle>();
        services.TryAddSingleton<AccessDenialAuditThrottle>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPermissionDenialRecorder, AccessDenialRecorder>());
        foreach (var action in AccessAuditActions.All)
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }

        return services;
    }
}
