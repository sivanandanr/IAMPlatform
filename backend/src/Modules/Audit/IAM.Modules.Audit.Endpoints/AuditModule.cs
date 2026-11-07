using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Audit.Endpoints;

/// <summary>
/// Composition entry point for the Audit module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// </summary>
public static class AuditModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/audit";

    public static IServiceCollection AddAuditModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints;
    }
}
