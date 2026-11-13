using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IAM.BuildingBlocks.Security;

/// <summary>
/// Decides whether a person may perform an action, from the current stored access model (ADR-0008, DL-059f). Answers
/// from the store every time; never from token claims, which can't see revocation (BACKEND_INPUT_VALIDATION).
/// Implemented by the Administration module.
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>True only if <paramref name="subject"/> may perform <paramref name="action"/> now. Unknown actions are false.</summary>
    Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken);
}

/// <summary>
/// Records a denied permission check as audit evidence (ADR-0008). Optional: when none is registered, denials are not
/// recorded. A failure to record never changes the outcome: the request is still refused.
/// </summary>
public interface IPermissionDenialRecorder
{
    Task RecordAsync(string subject, string action, string? routeTemplate, CancellationToken cancellationToken);
}

/// <summary>The endpoint needs one action id (for example <c>administration.roles.read</c>).</summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Action = action;
    }

    public string Action { get; }
}

/// <summary>
/// Endpoint metadata: authenticated <b>and</b> allowed the action. Combines with the default policy, so a missing or
/// invalid token is still 401 and a denied action is 403.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute, IAuthorizationRequirementData
{
    public RequirePermissionAttribute(string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Action = action;
    }

    public string Action { get; }

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return new PermissionRequirement(Action);
    }
}

public static class PermissionEndpointExtensions
{
    /// <summary>Requires an authenticated caller who may perform <paramref name="action"/> (checked per request, server-side).</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string action)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.RequireAuthorization(new RequirePermissionAttribute(action));
    }
}

public static class PermissionServices
{
    /// <summary>Registers the handler for <see cref="PermissionRequirement"/>. The host also needs an <see cref="IPermissionEvaluator"/>.</summary>
    public static IServiceCollection AddIamPermissionAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        return services;
    }
}

/// <summary>
/// Default deny: allowed only with an authenticated identity, a <c>sub</c>, and a yes from the evaluator. Anything the
/// evaluator throws (for example a damaged store) is not caught here, so it can never turn into a grant.
/// </summary>
internal sealed partial class PermissionAuthorizationHandler(
    IPermissionEvaluator evaluator,
    ILogger<PermissionAuthorizationHandler> logger,
    IEnumerable<IPermissionDenialRecorder> recorders) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var subject = context.User.FindFirst("sub")?.Value;
        if (context.User.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(subject))
        {
            context.Fail(new AuthorizationFailureReason(this, "No authenticated subject."));
            return;
        }

        var http = context.Resource as HttpContext;
        var cancellationToken = http?.RequestAborted ?? CancellationToken.None;
        if (await evaluator.CanAsync(subject, requirement.Action, cancellationToken).ConfigureAwait(false))
        {
            context.Succeed(requirement);
            return;
        }

        context.Fail(new AuthorizationFailureReason(this, "Permission denied."));
        var route = (http?.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        foreach (var recorder in recorders)
        {
            try
            {
                await recorder.RecordAsync(subject, requirement.Action, route, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Still refused. Only the exception type: its message can hold stored values.
                LogRecordFailed(logger, requirement.Action, ex.GetType().Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record a denied permission check for {Action} ({ExceptionType}); the request was refused.")]
    private static partial void LogRecordFailed(ILogger logger, string action, string exceptionType);
}
