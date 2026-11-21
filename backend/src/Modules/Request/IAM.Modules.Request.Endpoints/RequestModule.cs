using IAM.Modules.Request.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Request.Endpoints;

/// <summary>
/// Composition entry point for the Request module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// Registering the module makes the host require <c>ConnectionStrings:request</c> (ADR-0017).
/// </summary>
public static class RequestModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/request";

    public static IServiceCollection AddRequestModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddRequestInfrastructure();
        services.AddExceptionHandler<RequestExceptionHandler>();
        services.AddSingleton<FluentValidation.IValidator<SubmitItemBody>, SubmitItemBodyValidator>();
        return services;
    }

    public static IEndpointRouteBuilder MapRequestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // DL-091: request items, frozen when submitted and sent for approval through the outbox.
        RequestEndpoints.Map(endpoints);
        return endpoints;
    }
}
