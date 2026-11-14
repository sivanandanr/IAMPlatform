using IAM.Modules.Workflow.Application.Approvers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Workflow.Endpoints;

/// <summary>
/// Composition entry point for the Workflow module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// </summary>
public static class WorkflowModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/workflow";

    /// <summary>The configured fallback workgroup's key (DL-068c); <c>iam_managers</c> when not set.</summary>
    public const string FallbackWorkgroupSetting = "Workflow:FallbackWorkgroup";

    /// <summary>
    /// Registers approver resolution (DL-069). It reads the Catalog and Organization contracts, so the host must also add
    /// those modules. There are no endpoints yet: nothing calls resolution until workflow storage exists.
    /// </summary>
    public static IServiceCollection AddWorkflowModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new ApproverResolutionSettings(sp.GetRequiredService<IConfiguration>()[FallbackWorkgroupSetting]));
        services.TryAddScoped<IApproverResolver, ApproverResolver>();
        return services;
    }

    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints;
    }
}
