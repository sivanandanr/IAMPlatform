using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Policy.Endpoints;

/// <summary>
/// Composition entry point for the Policy module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// </summary>
public static class PolicyModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/policy";

    public static IServiceCollection AddPolicyModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }

    public static IEndpointRouteBuilder MapPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints;
    }
}
