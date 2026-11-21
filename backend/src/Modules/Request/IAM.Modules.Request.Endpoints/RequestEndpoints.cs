using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Request.Application.Items;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IAM.Modules.Request.Endpoints;

/// <summary>
/// A request item as the API accepts it (DL-091): for whom, which entitlement, and an existing account or a new one. The
/// requester is the signed-in person, never a field; nothing about the route, state or evidence is accepted.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitItemBody(string? Beneficiary, Guid? EntitlementId, Guid? AccountId, string? NewAccountType, string? NewAccountName, int? DurationDays);

public static class RequestApiLimits
{
    public const int PersonLength = 200;
    public const int NameLength = 256;
    public const int MaxBodyBytes = 4 * 1024;
}

public sealed class SubmitItemBodyValidator : AbstractValidator<SubmitItemBody>
{
    public SubmitItemBodyValidator()
    {
        RuleFor(b => b.Beneficiary).NotEmpty().MaximumLength(RequestApiLimits.PersonLength);
        RuleFor(b => b.EntitlementId).NotNull().NotEqual(Guid.Empty);
        RuleFor(b => b.AccountId).NotEqual(Guid.Empty);
        RuleFor(b => b.NewAccountType).MaximumLength(16);
        RuleFor(b => b.NewAccountName).MaximumLength(RequestApiLimits.NameLength);
        RuleFor(b => b.DurationDays).InclusiveBetween(1, 3650);
    }
}

internal sealed class RequestValidationFilter(IValidator<SubmitItemBody> validator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var body = context.Arguments.OfType<SubmitItemBody>().SingleOrDefault();
        if (body is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["A JSON body is required."] });
        }

        var errors = (await validator.ValidateAsync(body, context.HttpContext.RequestAborted).ConfigureAwait(false)).ToDictionary()
            .ToDictionary(e => JsonNamingPolicy.CamelCase.ConvertName(e.Key), e => e.Value, StringComparer.Ordinal);
        return errors.Count == 0 ? await next(context).ConfigureAwait(false) : TypedResults.ValidationProblem(errors);
    }
}

/// <summary>Request failures as ProblemDetails: a field and rule, or a refusal code; never people or content.</summary>
internal sealed class RequestExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var kind = RequestApi.Classify(exception, out var code, out var field, out var rule);
        if (kind == RequestFailureKind.None)
        {
            return false;
        }

        if (kind == RequestFailureKind.InvalidField)
        {
            var name = field is null ? "body" : JsonNamingPolicy.CamelCase.ConvertName(field);
            await Results.ValidationProblem(new Dictionary<string, string[]> { [name] = [rule ?? "is invalid"] }).ExecuteAsync(httpContext).ConfigureAwait(false);
            return true;
        }

        var (status, title) = kind switch
        {
            RequestFailureKind.Forbidden => (StatusCodes.Status403Forbidden, "The request was refused."),
            RequestFailureKind.WrongState => (StatusCodes.Status409Conflict, "The request was refused."),
            RequestFailureKind.Conflict => (StatusCodes.Status409Conflict, "Changed by someone else; try again."),
            RequestFailureKind.NotFound => (StatusCodes.Status404NotFound, "Not found."),
            RequestFailureKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "Requests are unavailable."),
            _ => (StatusCodes.Status422UnprocessableEntity, "The request was refused."),
        };
        httpContext.Response.StatusCode = status;
        var details = new ProblemDetails { Status = status, Title = title };
        if (code is not null)
        {
            details.Extensions["code"] = code;
        }

        await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = details, Exception = null }).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Request item endpoints (DL-091).</summary>
internal static class RequestEndpoints
{
    public const string ItemsPrefix = RequestModule.RoutePrefix + "/items";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var items = endpoints.MapGroup(ItemsPrefix).RequireAuthorization();
        items.MapGet(string.Empty, MineAsync).RequirePermission(AccessActions.RequestItemsSubmit).WithName("Request.MyItems");
        items.MapGet("/{id:guid}", GetAsync).RequirePermission(AccessActions.RequestItemsSubmit).WithName("Request.GetItem");
        items.MapGroup(string.Empty)
            .RequireBodyLimit(RequestApiLimits.MaxBodyBytes)
            .RequireRateLimiting(IamRateLimits.AdministrationWrites)
            .MapPost(string.Empty, SubmitAsync)
            .AddEndpointFilter<RequestValidationFilter>()
            .RequirePermission(AccessActions.RequestItemsSubmit)
            .WithName("Request.SubmitItem");
    }

    private static async Task<Ok<IReadOnlyList<RequestItemView>>> MineAsync(IRequestItems items, CancellationToken cancellationToken) =>
        TypedResults.Ok(await items.MineAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<RequestItemView>, NotFound>> GetAsync(Guid id, IRequestItems items, CancellationToken cancellationToken) =>
        await items.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound();

    private static async Task<Created<RequestItemView>> SubmitAsync(SubmitItemBody body, IRequestItems items, CancellationToken cancellationToken)
    {
        var item = await items.SubmitAsync(
            new SubmitItem(body.Beneficiary!, body.EntitlementId!.Value, body.AccountId, body.NewAccountType, body.NewAccountName, body.DurationDays), cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{ItemsPrefix}/{item.Id}", item);
    }
}
