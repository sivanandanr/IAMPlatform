using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Notification.Endpoints;

/// <summary>
/// Composition entry point for the Notification module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// </summary>
public static class NotificationModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/notification";

    public static IServiceCollection AddNotificationModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints;
    }
}
