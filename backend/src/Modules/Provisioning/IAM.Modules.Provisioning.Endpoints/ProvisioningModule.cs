using IAM.Modules.Provisioning.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Provisioning.Endpoints;

/// <summary>
/// Composition entry point for the Provisioning module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// Registering the module makes the host require <c>ConnectionStrings:provisioning</c> (ADR-0017): the API only reads items
/// for the IAM team (DL-094); planning and running are the provisioning worker's.
/// </summary>
public static class ProvisioningModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/provisioning";

    public static IServiceCollection AddProvisioningModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddProvisioningQueries();
        services.AddExceptionHandler<ProvisioningExceptionHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapProvisioningEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ProvisioningEndpoints.Map(endpoints);
        return endpoints;
    }
}
