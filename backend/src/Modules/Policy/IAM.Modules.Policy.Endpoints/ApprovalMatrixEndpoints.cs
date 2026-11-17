using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Policy.Application.Matrix;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IAM.Modules.Policy.Endpoints;

/// <summary>
/// A matrix draft as the API accepts it: the document as JSON (validated strictly by the domain) and, when a matrix exists,
/// the concurrency version the caller last read.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MatrixDraftBody(long? ExpectedConcurrencyVersion, JsonElement? Document);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MatrixReasonBody(string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApproveMatrixBody(string? ContentHash, string? Reason);

public static class MatrixApiLimits
{
    public const int ReasonLength = 1000;

    /// <summary>The domain's 4 MB document limit, plus room for the wrapper.</summary>
    public const int MaxDraftBodyBytes = (4 * 1024 * 1024) + 4096;

    public const int MaxBodyBytes = 16 * 1024;
}

public sealed class MatrixDraftBodyValidator : AbstractValidator<MatrixDraftBody>
{
    public MatrixDraftBodyValidator()
    {
        RuleFor(b => b.ExpectedConcurrencyVersion).GreaterThan(0);
        RuleFor(b => b.Document).NotNull().Must(d => d!.Value.ValueKind == JsonValueKind.Object).WithMessage("The document must be a JSON object.");
    }
}

public sealed class MatrixReasonBodyValidator : AbstractValidator<MatrixReasonBody>
{
    public MatrixReasonBodyValidator() => RuleFor(b => b.Reason).MaximumLength(MatrixApiLimits.ReasonLength);
}

public sealed class ApproveMatrixBodyValidator : AbstractValidator<ApproveMatrixBody>
{
    public ApproveMatrixBodyValidator()
    {
        RuleFor(b => b.ContentHash).NotEmpty().Length(64).Matches("^[0-9a-f]{64}$");
        RuleFor(b => b.Reason).MaximumLength(MatrixApiLimits.ReasonLength);
    }
}

internal sealed class MatrixValidationFilter<TBody>(IValidator<TBody> validator, bool reasonRequired = false) : IEndpointFilter
    where TBody : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var body = context.Arguments.OfType<TBody>().SingleOrDefault();
        if (body is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["A JSON body is required."] });
        }

        var errors = (await validator.ValidateAsync(body, context.HttpContext.RequestAborted).ConfigureAwait(false)).ToDictionary()
            .ToDictionary(e => JsonNamingPolicy.CamelCase.ConvertName(e.Key), e => e.Value, StringComparer.Ordinal);
        if (reasonRequired && body is MatrixReasonBody { Reason: var reason } && string.IsNullOrWhiteSpace(reason))
        {
            errors["reason"] = ["A reason is required."];
        }

        return errors.Count == 0 ? await next(context).ConfigureAwait(false) : TypedResults.ValidationProblem(errors);
    }
}

/// <summary>Matrix failures as ProblemDetails: a field and rule, or a refusal code; never the document's content.</summary>
internal sealed class MatrixExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var kind = MatrixApi.Classify(exception, out var code, out var field, out var rule);
        if (kind == MatrixFailureKind.None)
        {
            return false;
        }

        if (kind == MatrixFailureKind.InvalidField)
        {
            await Results.ValidationProblem(new Dictionary<string, string[]> { [field ?? "document"] = [rule ?? "is invalid"] }).ExecuteAsync(httpContext).ConfigureAwait(false);
            return true;
        }

        var (status, title) = kind switch
        {
            MatrixFailureKind.Forbidden => (StatusCodes.Status403Forbidden, "The approval matrix action was refused."),
            MatrixFailureKind.WrongState => (StatusCodes.Status409Conflict, "The approval matrix action was refused."),
            MatrixFailureKind.Conflict => (StatusCodes.Status409Conflict, "Changed by someone else; reload and try again."),
            MatrixFailureKind.NotFound => (StatusCodes.Status404NotFound, "Not found."),
            MatrixFailureKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "The approval matrix is unavailable."),
            _ => (StatusCodes.Status422UnprocessableEntity, "The approval matrix action was refused."),
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

/// <summary>Approval matrix endpoints (DL-090).</summary>
internal static class ApprovalMatrixEndpoints
{
    public const string MatrixPrefix = PolicyModule.RoutePrefix + "/matrix";
    public const string RequestsPrefix = PolicyModule.RoutePrefix + "/matrix-change-requests";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var matrix = endpoints.MapGroup(MatrixPrefix).RequireAuthorization();
        matrix.MapGet(string.Empty, GetAsync).RequirePermission(AccessActions.MatrixRead).WithName("Policy.GetMatrix");
        matrix.MapGet("/facts", ListFactsAsync).RequirePermission(AccessActions.MatrixRead).WithName("Policy.ListMatrixFacts");
        matrix.MapGroup(string.Empty)
            .RequireBodyLimit(MatrixApiLimits.MaxDraftBodyBytes)
            .RequireRateLimiting(IamRateLimits.AdministrationWrites)
            .MapPut("/draft", SetDraftAsync)
            .AddEndpointFilter<MatrixValidationFilter<MatrixDraftBody>>()
            .RequirePermission(AccessActions.MatrixManage)
            .WithName("Policy.SetMatrixDraft");
        Writes(matrix).MapPost("/change-requests", ProposeAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.MatrixManage)
            .WithName("Policy.ProposeMatrix");

        var requests = endpoints.MapGroup(RequestsPrefix).RequireAuthorization();
        requests.MapGet("/{id:guid}", ReviewAsync).RequirePermission(AccessActions.MatrixRead).WithName("Policy.GetMatrixChange");
        var decisions = Writes(requests);
        decisions.MapPost("/{id:guid}/approve", ApproveAsync)
            .AddEndpointFilter<MatrixValidationFilter<ApproveMatrixBody>>()
            .RequirePermission(AccessActions.MatrixApprove)
            .WithName("Policy.ApproveMatrix");
        decisions.MapPost("/{id:guid}/reject", RejectAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.MatrixApprove)
            .WithName("Policy.RejectMatrix");
        decisions.MapPost("/{id:guid}/withdraw", WithdrawAsync)
            .AddEndpointFilter<MatrixValidationFilter<MatrixReasonBody>>()
            .RequirePermission(AccessActions.MatrixManage)
            .WithName("Policy.WithdrawMatrix");
    }

    private static RouteGroupBuilder Writes(RouteGroupBuilder group) => group.MapGroup(string.Empty)
        .RequireBodyLimit(MatrixApiLimits.MaxBodyBytes)
        .RequireRateLimiting(IamRateLimits.AdministrationWrites);

    private static readonly Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> ReasonRequired = (factory, next) =>
    {
        var filter = new MatrixValidationFilter<MatrixReasonBody>(new MatrixReasonBodyValidator(), reasonRequired: true);
        return context => filter.InvokeAsync(context, next);
    };

    private static async Task<Results<Ok<ApprovalMatrixView>, NotFound>> GetAsync(IApprovalMatrixAdministration matrix, CancellationToken cancellationToken) =>
        await matrix.FindAsync(cancellationToken).ConfigureAwait(false) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound();

    private static async Task<Ok<IReadOnlyList<BuiltInFactView>>> ListFactsAsync(IApprovalMatrixAdministration matrix, CancellationToken cancellationToken) =>
        TypedResults.Ok(await matrix.ListFactsAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApprovalMatrixView>> SetDraftAsync(MatrixDraftBody body, IApprovalMatrixAdministration matrix, CancellationToken cancellationToken) =>
        TypedResults.Ok(await matrix.SetDraftAsync(body.ExpectedConcurrencyVersion, body.Document!.Value.GetRawText(), cancellationToken).ConfigureAwait(false));

    private static async Task<Created<MatrixChangeRequestView>> ProposeAsync(MatrixReasonBody body, IApprovalMatrixAdministration matrix, CancellationToken cancellationToken)
    {
        var request = await matrix.ProposeAsync(body.Reason!, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{RequestsPrefix}/{request.Id}", request);
    }

    private static async Task<Results<Ok<MatrixChangeReview>, NotFound>> ReviewAsync(Guid id, IApprovalMatrixAdministration matrix, CancellationToken cancellationToken) =>
        await matrix.ReviewAsync(id, cancellationToken).ConfigureAwait(false) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound();

    private static async Task<Ok<MatrixChangeRequestView>> ApproveAsync(Guid id, ApproveMatrixBody body, IApprovalMatrixAdministration matrix, CancellationToken cancellationToken) =>
        TypedResults.Ok(await matrix.ApproveAsync(id, body.ContentHash!, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<MatrixChangeRequestView>> RejectAsync(Guid id, MatrixReasonBody body, IApprovalMatrixAdministration matrix, CancellationToken cancellationToken) =>
        TypedResults.Ok(await matrix.RejectAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<MatrixChangeRequestView>> WithdrawAsync(Guid id, MatrixReasonBody body, IApprovalMatrixAdministration matrix, CancellationToken cancellationToken) =>
        TypedResults.Ok(await matrix.WithdrawAsync(id, body.Reason, cancellationToken).ConfigureAwait(false));
}
