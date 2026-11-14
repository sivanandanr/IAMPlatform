using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Workflow.Application.Approvals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IAM.Modules.Workflow.Endpoints;

// Bodies (DL-073). Unknown fields are refused, so there is no way to send "on behalf of" (DL-073e) or anything else the
// server decides: the actor is always the signed-in user.

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DecisionBody(long? ExpectedVersion, Guid? DecisionId, Guid? StageId, bool? Approve, string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompletionBody(long? ExpectedVersion, Guid? CompletionId, Guid? StageId, string? Note);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnBody(long? ExpectedVersion, Guid? StageId, string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VersionedReasonBody(long? ExpectedVersion, string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApproversBody(long? ExpectedVersion, IReadOnlyList<string?>? Approvers, string? Reason);

/// <summary>Bounds that match the engine's limits (ApprovalLimits); the engine checks every rule again.</summary>
public static class WorkflowApiLimits
{
    public const int PersonLength = 200;
    public const int MaxApprovers = 256;
    public const int ReasonLength = 1000;
    public const int MaxBodyBytes = 64 * 1024;
    public const int DefaultPageSize = 20;
}

public sealed class DecisionBodyValidator : AbstractValidator<DecisionBody>
{
    public DecisionBodyValidator()
    {
        RuleFor(b => b.ExpectedVersion).NotNull().GreaterThan(0);
        RuleFor(b => b.DecisionId).NotNull().NotEqual(Guid.Empty);
        RuleFor(b => b.StageId).NotNull().NotEqual(Guid.Empty);
        RuleFor(b => b.Approve).NotNull();
        RuleFor(b => b.Reason).MaximumLength(WorkflowApiLimits.ReasonLength);
        RuleFor(b => b.Reason).NotEmpty().When(b => b.Approve == false).WithMessage("A reason is required to reject.");
    }
}

public sealed class CompletionBodyValidator : AbstractValidator<CompletionBody>
{
    public CompletionBodyValidator()
    {
        RuleFor(b => b.ExpectedVersion).NotNull().GreaterThan(0);
        RuleFor(b => b.CompletionId).NotNull().NotEqual(Guid.Empty);
        RuleFor(b => b.StageId).NotNull().NotEqual(Guid.Empty);
        RuleFor(b => b.Note).MaximumLength(WorkflowApiLimits.ReasonLength);
    }
}

public sealed class ReturnBodyValidator : AbstractValidator<ReturnBody>
{
    public ReturnBodyValidator()
    {
        RuleFor(b => b.ExpectedVersion).NotNull().GreaterThan(0);
        RuleFor(b => b.StageId).NotNull().NotEqual(Guid.Empty);
        RuleFor(b => b.Reason).NotEmpty().MaximumLength(WorkflowApiLimits.ReasonLength);
    }
}

public sealed class VersionedReasonBodyValidator : AbstractValidator<VersionedReasonBody>
{
    public VersionedReasonBodyValidator()
    {
        RuleFor(b => b.ExpectedVersion).NotNull().GreaterThan(0);
        RuleFor(b => b.Reason).MaximumLength(WorkflowApiLimits.ReasonLength);
    }
}

public sealed class ApproversBodyValidator : AbstractValidator<ApproversBody>
{
    public ApproversBodyValidator()
    {
        RuleFor(b => b.ExpectedVersion).NotNull().GreaterThan(0);
        RuleFor(b => b.Approvers).Cascade(CascadeMode.Stop).NotNull()
            .Must(a => a!.Count is > 0 and <= WorkflowApiLimits.MaxApprovers).WithMessage($"Between 1 and {WorkflowApiLimits.MaxApprovers} people.")
            .Must(a => a!.All(p => PersonId.TryCreate(p, out _))).WithMessage("Must name each person by their id.");
        RuleFor(b => b.Reason).NotEmpty().MaximumLength(WorkflowApiLimits.ReasonLength);
    }
}

/// <summary>Runs the body's validator; field names as the JSON spells them. Optionally a reason is required.</summary>
internal sealed class WorkflowValidationFilter<TBody>(IValidator<TBody> validator, bool reasonRequired = false) : IEndpointFilter
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
        if (reasonRequired && body is VersionedReasonBody { Reason: var reason } && string.IsNullOrWhiteSpace(reason))
        {
            errors["reason"] = ["A reason is required."];
        }

        return errors.Count == 0 ? await next(context).ConfigureAwait(false) : TypedResults.ValidationProblem(errors);
    }
}

/// <summary>Workflow failures as ProblemDetails with a refusal code at most (DL-073d); never people, reasons or routes.</summary>
internal sealed class WorkflowExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var kind = WorkflowApi.Classify(exception, out var code);
        if (kind == WorkflowFailureKind.None)
        {
            return false;
        }

        var (status, title) = kind switch
        {
            WorkflowFailureKind.NotFound => (StatusCodes.Status404NotFound, "Not found."),
            WorkflowFailureKind.Forbidden => (StatusCodes.Status403Forbidden, "The approval action was refused."),
            WorkflowFailureKind.WrongState => (StatusCodes.Status409Conflict, "The approval action was refused."),
            WorkflowFailureKind.Conflict => (StatusCodes.Status409Conflict, "Changed by someone else; reload and try again."),
            WorkflowFailureKind.Damaged => (StatusCodes.Status409Conflict, "This approval request can't be used; it has been reported."),
            _ => (StatusCodes.Status422UnprocessableEntity, "The approval action was refused."),
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

internal static class ApprovalEndpoints
{
    public const string InstancesPrefix = WorkflowModule.RoutePrefix + "/instances";
    public const string TasksPath = WorkflowModule.RoutePrefix + "/my-tasks";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(TasksPath, MyTasksAsync)
            .RequireAuthorization()
            .RequirePermission(AccessActions.InstancesView)
            .WithName("Workflow.MyTasks");

        var instances = endpoints.MapGroup(InstancesPrefix).RequireAuthorization();
        instances.MapGet("/{id:guid}", GetAsync)
            .RequirePermission(AccessActions.InstancesView)
            .WithName("Workflow.GetInstance");

        var writes = instances.MapGroup(string.Empty)
            .RequireBodyLimit(WorkflowApiLimits.MaxBodyBytes)
            .RequireRateLimiting(IamRateLimits.AdministrationWrites);

        writes.MapPost("/{id:guid}/decisions", DecideAsync)
            .AddEndpointFilter<WorkflowValidationFilter<DecisionBody>>()
            .RequirePermission(AccessActions.ApprovalsAct)
            .WithName("Workflow.Decide");

        writes.MapPost("/{id:guid}/completions", CompleteAsync)
            .AddEndpointFilter<WorkflowValidationFilter<CompletionBody>>()
            .RequirePermission(AccessActions.ApprovalsAct)
            .WithName("Workflow.Complete");

        writes.MapPost("/{id:guid}/return", ReturnAsync)
            .AddEndpointFilter<WorkflowValidationFilter<ReturnBody>>()
            .RequirePermission(AccessActions.ApprovalsAct)
            .WithName("Workflow.Return");

        writes.MapPost("/{id:guid}/withdraw", WithdrawAsync)
            .AddEndpointFilter<WorkflowValidationFilter<VersionedReasonBody>>()
            .RequirePermission(AccessActions.ApprovalsAct)
            .WithName("Workflow.Withdraw");

        writes.MapPost("/{id:guid}/cancel", CancelAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.InstancesCancel)
            .WithName("Workflow.Cancel");

        writes.MapPost("/{id:guid}/stages/{stageId:guid}/escalation", EscalateAsync)
            .AddEndpointFilter<WorkflowValidationFilter<ApproversBody>>()
            .RequirePermission(AccessActions.ApprovalsEscalate)
            .WithName("Workflow.Escalate");

        writes.MapPost("/{id:guid}/stages/{stageId:guid}/escalation-proposals", ProposeAsync)
            .AddEndpointFilter<WorkflowValidationFilter<ApproversBody>>()
            .RequirePermission(AccessActions.ApprovalsEscalate)
            .WithName("Workflow.ProposeEscalation");

        writes.MapPost("/{id:guid}/stages/{stageId:guid}/escalation-proposals/approve", ApproveProposalAsync)
            .AddEndpointFilter<WorkflowValidationFilter<ApproversBody>>()
            .RequirePermission(AccessActions.ApprovalsEscalate)
            .WithName("Workflow.ApproveEscalation");

        writes.MapPost("/{id:guid}/stages/{stageId:guid}/escalation-proposals/withdraw", WithdrawProposalAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.ApprovalsEscalate)
            .WithName("Workflow.WithdrawEscalation");
    }

    private static readonly Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> ReasonRequired = (factory, next) =>
    {
        var filter = new WorkflowValidationFilter<VersionedReasonBody>(new VersionedReasonBodyValidator(), reasonRequired: true);
        return context => filter.InvokeAsync(context, next);
    };

    private static IReadOnlyList<PersonId> People(IReadOnlyList<string?> values) => [.. values.Select(v => PersonId.Create(v))];

    private static async Task<Results<Ok<ApprovalTaskPage>, ValidationProblem>> MyTasksAsync(IApprovalWorkflow workflow, CancellationToken cancellationToken, int? page = null, int? pageSize = null)
    {
        var p = page ?? 1;
        var size = pageSize ?? WorkflowApiLimits.DefaultPageSize;
        if (p is < 1 or > 1_000 || size is < 1 or > 100)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["page is 1 to 1000 and pageSize 1 to 100."] });
        }

        return TypedResults.Ok(await workflow.MyTasksAsync(p, size, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<ApprovalInstanceView>, NotFound>> GetAsync(Guid id, IApprovalWorkflow workflow, CancellationToken cancellationToken) =>
        await workflow.ReadAsync(id, cancellationToken).ConfigureAwait(false) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound();

    private static async Task<Ok<ApprovalInstanceView>> DecideAsync(Guid id, DecisionBody body, IApprovalWorkflow workflow, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workflow.DecideAsync(id, body.ExpectedVersion!.Value, body.DecisionId!.Value, body.StageId!.Value, null, body.Approve!.Value, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApprovalInstanceView>> CompleteAsync(Guid id, CompletionBody body, IApprovalWorkflow workflow, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workflow.CompleteAsync(id, body.ExpectedVersion!.Value, body.CompletionId!.Value, body.StageId!.Value, null, body.Note, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApprovalInstanceView>> ReturnAsync(Guid id, ReturnBody body, IApprovalWorkflow workflow, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workflow.ReturnForChangesAsync(id, body.ExpectedVersion!.Value, body.StageId!.Value, null, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApprovalInstanceView>> WithdrawAsync(Guid id, VersionedReasonBody body, IApprovalWorkflow workflow, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workflow.WithdrawAsync(id, body.ExpectedVersion!.Value, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApprovalInstanceView>> CancelAsync(Guid id, VersionedReasonBody body, IApprovalWorkflow workflow, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workflow.CancelAsync(id, body.ExpectedVersion!.Value, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApprovalInstanceView>> EscalateAsync(Guid id, Guid stageId, ApproversBody body, IApprovalWorkflow workflow, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workflow.EscalateAsync(id, body.ExpectedVersion!.Value, stageId, People(body.Approvers!), body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApprovalInstanceView>> ProposeAsync(Guid id, Guid stageId, ApproversBody body, IApprovalWorkflow workflow, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workflow.ProposeEscalationAsync(id, body.ExpectedVersion!.Value, stageId, People(body.Approvers!), body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApprovalInstanceView>> ApproveProposalAsync(Guid id, Guid stageId, ApproversBody body, IApprovalWorkflow workflow, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workflow.ApproveEscalationAsync(id, body.ExpectedVersion!.Value, stageId, People(body.Approvers!), body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApprovalInstanceView>> WithdrawProposalAsync(Guid id, Guid stageId, VersionedReasonBody body, IApprovalWorkflow workflow, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workflow.WithdrawEscalationAsync(id, body.ExpectedVersion!.Value, stageId, body.Reason, cancellationToken).ConfigureAwait(false));
}
