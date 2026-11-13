using System.Security.Claims;
using FluentValidation;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Application.Access;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Administration.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Administration.Endpoints;

/// <summary>
/// Composition entry point for the Administration module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// </summary>
public static class AdministrationModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/administration";

    /// <summary>
    /// Stored access control and the permission evaluator behind every endpoint check (P0-013). The host must also call
    /// <c>AddIamPermissionAuthorization</c>, <c>AddIamRateLimits</c>, <c>AddProblemDetails</c>, <c>UseExceptionHandler</c> and
    /// <c>UseRateLimiter</c>.
    /// </summary>
    public static IServiceCollection AddAdministrationModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAdministrationInfrastructure();
        services.AddExceptionHandler<AccessExceptionHandler>();
        services.AddSingleton<IValidator<ProposeChangeBody>, ProposeChangeBodyValidator>();
        services.AddSingleton<IValidator<DecisionBody>, DecisionBodyValidator>();
        services.AddSingleton<IValidator<RejectBody>, RejectBodyValidator>();
        return services;
    }

    public static IEndpointRouteBuilder MapAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(RoutePrefix);

        group.MapGet("/permissions/me", GetOwnPermissionsAsync)
            .RequirePermission(AccessActions.OwnPermissionsRead)
            .WithName("Administration.GetOwnPermissions");

        AccessApi.Map(group);

        return endpoints;
    }

    /// <summary>Your own roles and what they let you do, from the current model. Never anyone else's.</summary>
    private static async Task<Results<Ok<OwnPermissionsResponse>, ForbidHttpResult>> GetOwnPermissionsAsync(
        ClaimsPrincipal user, IAccessAdministration access, CancellationToken cancellationToken)
    {
        var subject = user.FindFirstValue("sub");
        if (string.IsNullOrEmpty(subject))
        {
            return TypedResults.Forbid();
        }

        var own = await access.OwnPermissionsAsync(subject, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new OwnPermissionsResponse(own.Roles, own.Actions, own.ModelVersion));
    }
}

/// <summary>Response of <c>GET /api/administration/permissions/me</c>: role codes and action ids only, no names of other people.</summary>
public sealed record OwnPermissionsResponse(IReadOnlyList<string> Roles, IReadOnlyList<string> Actions, long ModelVersion);
