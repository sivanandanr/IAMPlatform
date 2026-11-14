using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Organization.Application.Workgroups;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IAM.Modules.Organization.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterWorkgroupBody(string? Key, string? DisplayName, IReadOnlyList<string?>? Members);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RenameWorkgroupBody(long? ExpectedConcurrencyVersion, string? DisplayName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateMembersDraftBody(long? ExpectedConcurrencyVersion, IReadOnlyList<string?>? Members);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NewMembersDraftBody(IReadOnlyList<string?>? Members);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkgroupReasonBody(string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApproveMembersBody(string? ContentHash, string? Reason);

/// <summary>Bounds that match the workgroup rules (DL-068); the domain checks every rule again.</summary>
public static class WorkgroupApiLimits
{
    public const int KeyLength = 64;
    public const int DisplayNameLength = 200;
    public const int PersonLength = 200;
    public const int MaxMembers = 256;
    public const int ReasonLength = 1000;
    public const int MaxBodyBytes = 64 * 1024;
}

public sealed class RegisterWorkgroupBodyValidator : AbstractValidator<RegisterWorkgroupBody>
{
    public RegisterWorkgroupBodyValidator()
    {
        RuleFor(b => b.Key).NotEmpty().MaximumLength(WorkgroupApiLimits.KeyLength);
        RuleFor(b => b.DisplayName).NotEmpty().MaximumLength(WorkgroupApiLimits.DisplayNameLength);
        RuleFor(b => b.Members).Cascade(CascadeMode.Stop).NotNull().Must(m => m!.Count <= WorkgroupApiLimits.MaxMembers)
            .WithMessage($"At most {WorkgroupApiLimits.MaxMembers} members.");
        RuleForEach(b => b.Members).NotEmpty().MaximumLength(WorkgroupApiLimits.PersonLength);
    }
}

public sealed class RenameWorkgroupBodyValidator : AbstractValidator<RenameWorkgroupBody>
{
    public RenameWorkgroupBodyValidator()
    {
        RuleFor(b => b.ExpectedConcurrencyVersion).NotNull().GreaterThan(0);
        RuleFor(b => b.DisplayName).NotEmpty().MaximumLength(WorkgroupApiLimits.DisplayNameLength);
    }
}

public sealed class UpdateMembersDraftBodyValidator : AbstractValidator<UpdateMembersDraftBody>
{
    public UpdateMembersDraftBodyValidator()
    {
        RuleFor(b => b.ExpectedConcurrencyVersion).NotNull().GreaterThan(0);
        RuleFor(b => b.Members).Cascade(CascadeMode.Stop).NotNull().Must(m => m!.Count <= WorkgroupApiLimits.MaxMembers)
            .WithMessage($"At most {WorkgroupApiLimits.MaxMembers} members.");
        RuleForEach(b => b.Members).NotEmpty().MaximumLength(WorkgroupApiLimits.PersonLength);
    }
}

public sealed class NewMembersDraftBodyValidator : AbstractValidator<NewMembersDraftBody>
{
    public NewMembersDraftBodyValidator()
    {
        RuleFor(b => b.Members).Cascade(CascadeMode.Stop).NotNull().Must(m => m!.Count <= WorkgroupApiLimits.MaxMembers)
            .WithMessage($"At most {WorkgroupApiLimits.MaxMembers} members.");
        RuleForEach(b => b.Members).NotEmpty().MaximumLength(WorkgroupApiLimits.PersonLength);
    }
}

public sealed class WorkgroupReasonBodyValidator : AbstractValidator<WorkgroupReasonBody>
{
    public WorkgroupReasonBodyValidator() => RuleFor(b => b.Reason).MaximumLength(WorkgroupApiLimits.ReasonLength);
}

public sealed class ApproveMembersBodyValidator : AbstractValidator<ApproveMembersBody>
{
    public ApproveMembersBodyValidator()
    {
        RuleFor(b => b.ContentHash).NotEmpty().Length(64).Matches("^[0-9a-f]{64}$");
        RuleFor(b => b.Reason).MaximumLength(WorkgroupApiLimits.ReasonLength);
    }
}

/// <summary>Runs the body's validator on every request; field names as the JSON spells them (camelCase).</summary>
internal sealed class WorkgroupValidationFilter<TBody>(IValidator<TBody> validator, bool reasonRequired = false) : IEndpointFilter
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
        if (reasonRequired && body is WorkgroupReasonBody { Reason: var reason } && string.IsNullOrWhiteSpace(reason))
        {
            errors["reason"] = ["A reason is required."];
        }

        return errors.Count == 0 ? await next(context).ConfigureAwait(false) : TypedResults.ValidationProblem(errors);
    }
}

/// <summary>Workgroup failures as ProblemDetails: a field and rule, or a refusal code, at most; never people or reasons.</summary>
internal sealed class WorkgroupExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var kind = WorkgroupApi.Classify(exception, out var code, out var field, out var rule);
        if (kind == WorkgroupFailureKind.None)
        {
            return false;
        }

        if (kind == WorkgroupFailureKind.InvalidField)
        {
            var name = field is null ? "body" : JsonNamingPolicy.CamelCase.ConvertName(field);
            await Results.ValidationProblem(new Dictionary<string, string[]> { [name] = [rule ?? "is invalid"] }).ExecuteAsync(httpContext).ConfigureAwait(false);
            return true;
        }

        var (status, title) = kind switch
        {
            WorkgroupFailureKind.Forbidden => (StatusCodes.Status403Forbidden, "The workgroup action was refused."),
            WorkgroupFailureKind.WrongState => (StatusCodes.Status409Conflict, "The workgroup action was refused."),
            WorkgroupFailureKind.Conflict => (StatusCodes.Status409Conflict, "Changed by someone else; reload and try again."),
            WorkgroupFailureKind.NotFound => (StatusCodes.Status404NotFound, "Not found."),
            WorkgroupFailureKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "Workgroups are unavailable."),
            _ => (StatusCodes.Status422UnprocessableEntity, "The workgroup action was refused."),
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

internal static class WorkgroupEndpoints
{
    public const string WorkgroupsPrefix = OrganizationModule.RoutePrefix + "/workgroups";
    public const string RequestsPrefix = OrganizationModule.RoutePrefix + "/workgroup-change-requests";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var workgroups = endpoints.MapGroup(WorkgroupsPrefix).RequireAuthorization();
        workgroups.MapGet("/{key}", GetAsync)
            .RequirePermission(AccessActions.WorkgroupsRead)
            .WithName("Organization.GetWorkgroup");

        var writes = Writes(workgroups);
        writes.MapPost(string.Empty, RegisterAsync)
            .AddEndpointFilter<WorkgroupValidationFilter<RegisterWorkgroupBody>>()
            .RequirePermission(AccessActions.WorkgroupsManage)
            .WithName("Organization.RegisterWorkgroup");

        writes.MapPut("/{id:guid}/name", RenameAsync)
            .AddEndpointFilter<WorkgroupValidationFilter<RenameWorkgroupBody>>()
            .RequirePermission(AccessActions.WorkgroupsManage)
            .WithName("Organization.RenameWorkgroup");

        writes.MapPut("/{id:guid}/draft", UpdateDraftAsync)
            .AddEndpointFilter<WorkgroupValidationFilter<UpdateMembersDraftBody>>()
            .RequirePermission(AccessActions.WorkgroupsManage)
            .WithName("Organization.UpdateWorkgroupDraft");

        writes.MapPost("/{id:guid}/drafts", StartNewDraftAsync)
            .AddEndpointFilter<WorkgroupValidationFilter<NewMembersDraftBody>>()
            .RequirePermission(AccessActions.WorkgroupsManage)
            .WithName("Organization.StartWorkgroupDraft");

        writes.MapPost("/{id:guid}/change-requests", ProposeAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.WorkgroupsManage)
            .WithName("Organization.ProposeWorkgroupChange");

        var requests = endpoints.MapGroup(RequestsPrefix).RequireAuthorization();
        requests.MapGet("/{id:guid}", GetRequestAsync)
            .RequirePermission(AccessActions.WorkgroupsRead)
            .WithName("Organization.GetWorkgroupChange");

        var decisions = Writes(requests);
        decisions.MapPost("/{id:guid}/approve", ApproveAsync)
            .AddEndpointFilter<WorkgroupValidationFilter<ApproveMembersBody>>()
            .RequirePermission(AccessActions.WorkgroupsApprove)
            .WithName("Organization.ApproveWorkgroupChange");

        decisions.MapPost("/{id:guid}/reject", RejectAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.WorkgroupsApprove)
            .WithName("Organization.RejectWorkgroupChange");

        decisions.MapPost("/{id:guid}/withdraw", WithdrawAsync)
            .AddEndpointFilter<WorkgroupValidationFilter<WorkgroupReasonBody>>()
            .RequirePermission(AccessActions.WorkgroupsManage)
            .WithName("Organization.WithdrawWorkgroupChange");
    }

    private static RouteGroupBuilder Writes(RouteGroupBuilder group) => group.MapGroup(string.Empty)
        .RequireBodyLimit(WorkgroupApiLimits.MaxBodyBytes)
        .RequireRateLimiting(IamRateLimits.AdministrationWrites);

    private static readonly Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> ReasonRequired = (factory, next) =>
    {
        var filter = new WorkgroupValidationFilter<WorkgroupReasonBody>(new WorkgroupReasonBodyValidator(), reasonRequired: true);
        return context => filter.InvokeAsync(context, next);
    };

    private static async Task<Results<Ok<WorkgroupView>, NotFound>> GetAsync(string key, IWorkgroupAdministration workgroups, CancellationToken cancellationToken) =>
        await workgroups.FindAsync(key, cancellationToken).ConfigureAwait(false) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound();

    private static async Task<Created<WorkgroupView>> RegisterAsync(RegisterWorkgroupBody body, IWorkgroupAdministration workgroups, CancellationToken cancellationToken)
    {
        var created = await workgroups.RegisterAsync(new RegisterWorkgroupRequest(body.Key!, body.DisplayName!, body.Members!), cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{WorkgroupsPrefix}/{created.Key}", created);
    }

    private static async Task<Ok<WorkgroupView>> RenameAsync(Guid id, RenameWorkgroupBody body, IWorkgroupAdministration workgroups, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workgroups.RenameAsync(id, body.ExpectedConcurrencyVersion!.Value, body.DisplayName!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<WorkgroupView>> UpdateDraftAsync(Guid id, UpdateMembersDraftBody body, IWorkgroupAdministration workgroups, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workgroups.UpdateDraftAsync(id, body.ExpectedConcurrencyVersion!.Value, body.Members!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<WorkgroupView>> StartNewDraftAsync(Guid id, NewMembersDraftBody body, IWorkgroupAdministration workgroups, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workgroups.StartNewDraftAsync(id, body.Members!, cancellationToken).ConfigureAwait(false));

    private static async Task<Created<WorkgroupChangeRequestView>> ProposeAsync(Guid id, WorkgroupReasonBody body, IWorkgroupAdministration workgroups, CancellationToken cancellationToken)
    {
        var request = await workgroups.ProposeAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{RequestsPrefix}/{request.Id}", request);
    }

    private static async Task<Results<Ok<WorkgroupChangeReview>, NotFound>> GetRequestAsync(Guid id, IWorkgroupAdministration workgroups, CancellationToken cancellationToken) =>
        await workgroups.ReviewRequestAsync(id, cancellationToken).ConfigureAwait(false) is { } review ? TypedResults.Ok(review) : TypedResults.NotFound();

    private static async Task<Ok<WorkgroupChangeRequestView>> ApproveAsync(Guid id, ApproveMembersBody body, IWorkgroupAdministration workgroups, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workgroups.ApproveAsync(id, body.ContentHash!, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<WorkgroupChangeRequestView>> RejectAsync(Guid id, WorkgroupReasonBody body, IWorkgroupAdministration workgroups, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workgroups.RejectAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<WorkgroupChangeRequestView>> WithdrawAsync(Guid id, WorkgroupReasonBody body, IWorkgroupAdministration workgroups, CancellationToken cancellationToken) =>
        TypedResults.Ok(await workgroups.WithdrawAsync(id, body.Reason, cancellationToken).ConfigureAwait(false));
}
