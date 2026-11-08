using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Identity.Endpoints;

/// <summary>
/// Composition entry point for the Identity module. The host calls these methods only;
/// it never references the module's Application, Domain or Infrastructure directly (ADR-0001).
/// </summary>
public static class IdentityModule
{
    /// <summary>Route prefix reserved for this module's endpoints.</summary>
    public const string RoutePrefix = "/api/identity";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).RequireAuthorization();

        // S1-05 secured sample endpoint: returns only the caller's subject identifier (no profile/PII).
        group.MapGet("/me", GetCurrentSubject).WithName("Identity.GetCurrentSubject");

        return endpoints;
    }

    private static IResult GetCurrentSubject(ClaimsPrincipal user)
    {
        var subject = user.FindFirstValue("sub");
        return string.IsNullOrEmpty(subject)
            ? TypedResults.Forbid()
            : TypedResults.Ok(new CurrentSubjectResponse(subject));
    }
}

/// <summary>Response of <c>GET /api/identity/me</c>.</summary>
public sealed record CurrentSubjectResponse(string Subject);
