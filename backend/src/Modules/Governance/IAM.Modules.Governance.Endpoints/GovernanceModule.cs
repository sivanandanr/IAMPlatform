using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Governance.Endpoints;

/// <summary>
/// Composition entry point for the Governance module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// </summary>
public static class GovernanceModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/governance";

    public static IServiceCollection AddGovernanceModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }

    public static IEndpointRouteBuilder MapGovernanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints;
    }
}
