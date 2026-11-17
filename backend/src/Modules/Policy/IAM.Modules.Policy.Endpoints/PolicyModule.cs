using IAM.Modules.Policy.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Policy.Endpoints;

/// <summary>
/// Composition entry point for the Policy module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// Registering the module makes the host require <c>ConnectionStrings:policy</c> (ADR-0017).
/// </summary>
public static class PolicyModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/policy";

    public static IServiceCollection AddPolicyModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPolicyInfrastructure();
        services.AddExceptionHandler<MatrixExceptionHandler>();
        services.AddSingleton<FluentValidation.IValidator<MatrixDraftBody>, MatrixDraftBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<MatrixReasonBody>, MatrixReasonBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<ApproveMatrixBody>, ApproveMatrixBodyValidator>();
        return services;
    }

    public static IEndpointRouteBuilder MapPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // DL-090: the enterprise approval matrix, activated by a second person.
        ApprovalMatrixEndpoints.Map(endpoints);
        return endpoints;
    }
}
