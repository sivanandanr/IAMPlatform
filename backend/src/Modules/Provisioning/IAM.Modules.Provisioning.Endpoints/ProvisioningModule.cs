using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Provisioning.Endpoints;

/// <summary>
/// Composition entry point for the Provisioning module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// </summary>
public static class ProvisioningModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/provisioning";

    public static IServiceCollection AddProvisioningModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }

    public static IEndpointRouteBuilder MapProvisioningEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints;
    }
}
