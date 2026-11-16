using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Account.Application.Accounts;
using IAM.Modules.Administration.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IAM.Modules.Account.Endpoints;

/// <summary>A link request as the API accepts it. Server-owned fields are never accepted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProposeLinkBody(Guid? ApplicationId, string? Type, string? Name, string? TargetId, string? Owner, string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NamePartBody(string? Literal, string? Attribute, IReadOnlyList<string?>? Transforms, int? Length);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NamingDraftBody(long? ExpectedConcurrencyVersion, IReadOnlyList<NamePartBody?>? Parts, int? MaxLength);

/// <summary>A reason: required to propose or reject, optional to approve or withdraw.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountReasonBody(string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApproveNamingBody(string? ContentHash, string? Reason);

public static class AccountApiLimits
{
    public const int PersonLength = 200;
    public const int CodeLength = 16;
    public const int NameLength = 256;
    public const int TargetIdLength = 512;
    public const int ReasonLength = 1000;
    public const int MaxParts = 10;
    public const int LiteralLength = 20;
    public const int AttributeLength = 64;
    public const int MaxTransforms = 5;
    public const int MaxBodyBytes = 16 * 1024;
}

public sealed class ProposeLinkBodyValidator : AbstractValidator<ProposeLinkBody>
{
    public ProposeLinkBodyValidator()
    {
        RuleFor(b => b.ApplicationId).NotNull().NotEqual(Guid.Empty);
        RuleFor(b => b.Type).NotEmpty().MaximumLength(AccountApiLimits.CodeLength);
        RuleFor(b => b.Name).NotEmpty().MaximumLength(AccountApiLimits.NameLength);
        RuleFor(b => b.TargetId).NotEmpty().MaximumLength(AccountApiLimits.TargetIdLength);
        RuleFor(b => b.Owner).NotEmpty().MaximumLength(AccountApiLimits.PersonLength);
        RuleFor(b => b.Reason).NotEmpty().MaximumLength(AccountApiLimits.ReasonLength);
    }
}

public sealed class NamePartBodyValidator : AbstractValidator<NamePartBody>
{
    public NamePartBodyValidator()
    {
        RuleFor(b => b.Literal).MaximumLength(AccountApiLimits.LiteralLength);
        RuleFor(b => b.Attribute).MaximumLength(AccountApiLimits.AttributeLength);
        RuleFor(b => b.Transforms).Must(t => t is null || t.Count <= AccountApiLimits.MaxTransforms).WithMessage($"At most {AccountApiLimits.MaxTransforms} transforms.");
        RuleForEach(b => b.Transforms).NotEmpty().MaximumLength(32);
        RuleFor(b => b.Length).InclusiveBetween(1, 64);
    }
}

public sealed class NamingDraftBodyValidator : AbstractValidator<NamingDraftBody>
{
    public NamingDraftBodyValidator()
    {
        RuleFor(b => b.ExpectedConcurrencyVersion).GreaterThan(0);
        RuleFor(b => b.Parts).Cascade(CascadeMode.Stop).NotNull().NotEmpty()
            .Must(p => p!.Count <= AccountApiLimits.MaxParts).WithMessage($"At most {AccountApiLimits.MaxParts} parts.");
        RuleForEach(b => b.Parts).NotNull().SetValidator(new NamePartBodyValidator()!);
        RuleFor(b => b.MaxLength).NotNull().InclusiveBetween(1, AccountApiLimits.NameLength);
    }
}

public sealed class AccountReasonBodyValidator : AbstractValidator<AccountReasonBody>
{
    public AccountReasonBodyValidator() => RuleFor(b => b.Reason).MaximumLength(AccountApiLimits.ReasonLength);
}

public sealed class ApproveNamingBodyValidator : AbstractValidator<ApproveNamingBody>
{
    public ApproveNamingBodyValidator()
    {
        RuleFor(b => b.ContentHash).NotEmpty().Length(64).Matches("^[0-9a-f]{64}$");
        RuleFor(b => b.Reason).MaximumLength(AccountApiLimits.ReasonLength);
    }
}

/// <summary>Runs the body's validator on every request; field names as the JSON spells them (camelCase).</summary>
internal sealed class AccountValidationFilter<TBody>(IValidator<TBody> validator, bool reasonRequired = false) : IEndpointFilter
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
        if (reasonRequired && body is AccountReasonBody { Reason: var reason } && string.IsNullOrWhiteSpace(reason))
        {
            errors["reason"] = ["A reason is required."];
        }

        return errors.Count == 0 ? await next(context).ConfigureAwait(false) : TypedResults.ValidationProblem(errors);
    }
}

/// <summary>Account failures as ProblemDetails: a field name and rule, or a refusal code; never stored values or people.</summary>
internal sealed class AccountExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var kind = AccountApi.Classify(exception, out var code, out var field, out var rule);
        if (kind == AccountFailureKind.None)
        {
            return false;
        }

        if (kind == AccountFailureKind.InvalidField)
        {
            var name = field is null ? "body" : JsonNamingPolicy.CamelCase.ConvertName(field);
            await Results.ValidationProblem(new Dictionary<string, string[]> { [name] = [rule ?? "is invalid"] }).ExecuteAsync(httpContext).ConfigureAwait(false);
            return true;
        }

        var (status, title) = kind switch
        {
            AccountFailureKind.Forbidden => (StatusCodes.Status403Forbidden, "The account action was refused."),
            AccountFailureKind.WrongState => (StatusCodes.Status409Conflict, "The account action was refused."),
            AccountFailureKind.Conflict => (StatusCodes.Status409Conflict, "Changed by someone else; reload and try again."),
            AccountFailureKind.NotFound => (StatusCodes.Status404NotFound, "Not found."),
            AccountFailureKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "Accounts are unavailable."),
            _ => (StatusCodes.Status422UnprocessableEntity, "The account action was refused."),
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

/// <summary>Account endpoints (DL-089): account reads, link requests and naming rules.</summary>
internal static class AccountEndpoints
{
    public const string AccountsPrefix = AccountModule.RoutePrefix + "/accounts";
    public const string LinkRequestsPrefix = AccountModule.RoutePrefix + "/link-requests";
    public const string ApplicationsPrefix = AccountModule.RoutePrefix + "/applications";
    public const string NamingRequestsPrefix = AccountModule.RoutePrefix + "/naming-rule-change-requests";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var accounts = endpoints.MapGroup(AccountsPrefix).RequireAuthorization();
        accounts.MapGet("/{id:guid}", GetAccountAsync).RequirePermission(AccessActions.AccountsRead).WithName("Account.GetAccount");

        var applications = endpoints.MapGroup(ApplicationsPrefix).RequireAuthorization();
        applications.MapGet("/{applicationId:guid}/accounts", ListAccountsAsync).RequirePermission(AccessActions.AccountsRead).WithName("Account.ListAccounts");
        applications.MapGet("/{applicationId:guid}/naming-rules/{accountType}", GetNamingRuleAsync).RequirePermission(AccessActions.AccountsRead).WithName("Account.GetNamingRule");
        var ruleWrites = Writes(applications);
        ruleWrites.MapPut("/{applicationId:guid}/naming-rules/{accountType}/draft", SetNamingDraftAsync)
            .AddEndpointFilter<AccountValidationFilter<NamingDraftBody>>()
            .RequirePermission(AccessActions.AccountsManage)
            .WithName("Account.SetNamingDraft");
        ruleWrites.MapPost("/{applicationId:guid}/naming-rules/{accountType}/change-requests", ProposeNamingAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.AccountsManage)
            .WithName("Account.ProposeNamingRule");

        var links = endpoints.MapGroup(LinkRequestsPrefix).RequireAuthorization();
        links.MapGet("/{id:guid}", GetLinkAsync).RequirePermission(AccessActions.AccountsRead).WithName("Account.GetLinkRequest");
        var linkWrites = Writes(links);
        linkWrites.MapPost(string.Empty, ProposeLinkAsync)
            .AddEndpointFilter<AccountValidationFilter<ProposeLinkBody>>()
            .RequirePermission(AccessActions.AccountsManage)
            .WithName("Account.ProposeLink");
        linkWrites.MapPost("/{id:guid}/approve", ApproveLinkAsync)
            .AddEndpointFilter<AccountValidationFilter<AccountReasonBody>>()
            .RequirePermission(AccessActions.AccountsApprove)
            .WithName("Account.ApproveLink");
        linkWrites.MapPost("/{id:guid}/reject", RejectLinkAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.AccountsApprove)
            .WithName("Account.RejectLink");
        linkWrites.MapPost("/{id:guid}/withdraw", WithdrawLinkAsync)
            .AddEndpointFilter<AccountValidationFilter<AccountReasonBody>>()
            .RequirePermission(AccessActions.AccountsManage)
            .WithName("Account.WithdrawLink");

        var naming = endpoints.MapGroup(NamingRequestsPrefix).RequireAuthorization();
        naming.MapGet("/{id:guid}", GetNamingRequestAsync).RequirePermission(AccessActions.AccountsRead).WithName("Account.GetNamingRuleChange");
        var namingWrites = Writes(naming);
        namingWrites.MapPost("/{id:guid}/approve", ApproveNamingAsync)
            .AddEndpointFilter<AccountValidationFilter<ApproveNamingBody>>()
            .RequirePermission(AccessActions.AccountsApprove)
            .WithName("Account.ApproveNamingRule");
        namingWrites.MapPost("/{id:guid}/reject", RejectNamingAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.AccountsApprove)
            .WithName("Account.RejectNamingRule");
        namingWrites.MapPost("/{id:guid}/withdraw", WithdrawNamingAsync)
            .AddEndpointFilter<AccountValidationFilter<AccountReasonBody>>()
            .RequirePermission(AccessActions.AccountsManage)
            .WithName("Account.WithdrawNamingRule");
    }

    private static RouteGroupBuilder Writes(RouteGroupBuilder group) => group.MapGroup(string.Empty)
        .RequireBodyLimit(AccountApiLimits.MaxBodyBytes)
        .RequireRateLimiting(IamRateLimits.AdministrationWrites);

    private static readonly Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> ReasonRequired = (factory, next) =>
    {
        var filter = new AccountValidationFilter<AccountReasonBody>(new AccountReasonBodyValidator(), reasonRequired: true);
        return context => filter.InvokeAsync(context, next);
    };

    private static async Task<Results<Ok<AccountView>, NotFound>> GetAccountAsync(Guid id, IAccountAdministration accounts, CancellationToken cancellationToken) =>
        await accounts.FindAccountAsync(id, cancellationToken).ConfigureAwait(false) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound();

    private static async Task<Ok<IReadOnlyList<AccountView>>> ListAccountsAsync(Guid applicationId, string? owner, IAccountAdministration accounts, CancellationToken cancellationToken) =>
        TypedResults.Ok(await accounts.ListAccountsAsync(applicationId, owner, cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<NamingRuleView>, NotFound>> GetNamingRuleAsync(Guid applicationId, string accountType, IAccountAdministration accounts, CancellationToken cancellationToken) =>
        await accounts.FindNamingRuleAsync(applicationId, accountType, cancellationToken).ConfigureAwait(false) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound();

    private static async Task<Ok<NamingRuleView>> SetNamingDraftAsync(Guid applicationId, string accountType, NamingDraftBody body, IAccountAdministration accounts, CancellationToken cancellationToken) =>
        TypedResults.Ok(await accounts.SetNamingDraftAsync(
            applicationId,
            accountType,
            body.ExpectedConcurrencyVersion,
            [.. body.Parts!.Select(p => p is null ? null : new NamePartText(p.Literal, p.Attribute, p.Transforms ?? [], p.Length))],
            body.MaxLength!.Value,
            cancellationToken).ConfigureAwait(false));

    private static async Task<Created<NamingRuleChangeRequestView>> ProposeNamingAsync(Guid applicationId, string accountType, AccountReasonBody body, IAccountAdministration accounts, CancellationToken cancellationToken)
    {
        var request = await accounts.ProposeNamingRuleAsync(applicationId, accountType, body.Reason!, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{NamingRequestsPrefix}/{request.Id}", request);
    }

    private static async Task<Results<Ok<AccountLinkRequestView>, NotFound>> GetLinkAsync(Guid id, IAccountAdministration accounts, CancellationToken cancellationToken) =>
        await accounts.FindLinkRequestAsync(id, cancellationToken).ConfigureAwait(false) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound();

    private static async Task<Created<AccountLinkRequestView>> ProposeLinkAsync(ProposeLinkBody body, IAccountAdministration accounts, CancellationToken cancellationToken)
    {
        var request = await accounts.ProposeLinkAsync(body.ApplicationId!.Value, body.Type!, body.Name!, body.TargetId!, body.Owner!, body.Reason!, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{LinkRequestsPrefix}/{request.Id}", request);
    }

    private static async Task<Ok<AccountLinkRequestView>> ApproveLinkAsync(Guid id, AccountReasonBody body, IAccountAdministration accounts, CancellationToken cancellationToken) =>
        TypedResults.Ok(await accounts.ApproveLinkAsync(id, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<AccountLinkRequestView>> RejectLinkAsync(Guid id, AccountReasonBody body, IAccountAdministration accounts, CancellationToken cancellationToken) =>
        TypedResults.Ok(await accounts.RejectLinkAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<AccountLinkRequestView>> WithdrawLinkAsync(Guid id, AccountReasonBody body, IAccountAdministration accounts, CancellationToken cancellationToken) =>
        TypedResults.Ok(await accounts.WithdrawLinkAsync(id, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<NamingRuleChangeReview>, NotFound>> GetNamingRequestAsync(Guid id, IAccountAdministration accounts, CancellationToken cancellationToken) =>
        await accounts.ReviewNamingRequestAsync(id, cancellationToken).ConfigureAwait(false) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound();

    private static async Task<Ok<NamingRuleChangeRequestView>> ApproveNamingAsync(Guid id, ApproveNamingBody body, IAccountAdministration accounts, CancellationToken cancellationToken) =>
        TypedResults.Ok(await accounts.ApproveNamingRuleAsync(id, body.ContentHash!, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<NamingRuleChangeRequestView>> RejectNamingAsync(Guid id, AccountReasonBody body, IAccountAdministration accounts, CancellationToken cancellationToken) =>
        TypedResults.Ok(await accounts.RejectNamingRuleAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<NamingRuleChangeRequestView>> WithdrawNamingAsync(Guid id, AccountReasonBody body, IAccountAdministration accounts, CancellationToken cancellationToken) =>
        TypedResults.Ok(await accounts.WithdrawNamingRuleAsync(id, body.Reason, cancellationToken).ConfigureAwait(false));
}
