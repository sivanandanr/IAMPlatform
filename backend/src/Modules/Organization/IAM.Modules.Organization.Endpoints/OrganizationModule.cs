using IAM.Modules.Organization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Organization.Endpoints;

/// <summary>
/// Composition entry point for the Organization module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// Registering the module makes the host require <c>ConnectionStrings:organization</c> (ADR-0017).
/// </summary>
public static class OrganizationModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/organization";

    public static IServiceCollection AddOrganizationModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOrganizationInfrastructure();
        services.AddExceptionHandler<WorkgroupExceptionHandler>();
        services.AddSingleton<FluentValidation.IValidator<RegisterWorkgroupBody>, RegisterWorkgroupBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<RenameWorkgroupBody>, RenameWorkgroupBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<UpdateMembersDraftBody>, UpdateMembersDraftBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<NewMembersDraftBody>, NewMembersDraftBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<WorkgroupReasonBody>, WorkgroupReasonBodyValidator>();
        services.AddSingleton<FluentValidation.IValidator<ApproveMembersBody>, ApproveMembersBodyValidator>();
        return services;
    }

    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // DL-068: IAM workgroups, with member changes decided by a second person who is not a member.
        WorkgroupEndpoints.Map(endpoints);
        return endpoints;
    }
}
