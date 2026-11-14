using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Catalog.Application.Applications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IAM.Modules.Catalog.Endpoints;

/// <summary>One application role as the API accepts it. Server-owned fields (ids, state, hash, version) are never accepted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApplicationRoleBody(string? RoleTypeCode, string? Requirement, string? HolderKind, string? HolderId);

/// <summary>An application's governed settings as the API accepts them: the owner and the roles.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApplicationSettingsBody(string? Owner, IReadOnlyList<ApplicationRoleBody?>? Roles)
{
    public ApplicationSettingsInput ToInput() =>
        CatalogApi.ToSettings(Owner, Roles?.Select(r => r is null ? null : new ApplicationRoleText(r.RoleTypeCode, r.Requirement, r.HolderKind, r.HolderId)).ToList());
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterApplicationBody(string? Key, string? DisplayName, ApplicationSettingsBody? Settings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RenameApplicationBody(long? ExpectedConcurrencyVersion, string? DisplayName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateSettingsDraftBody(long? ExpectedConcurrencyVersion, ApplicationSettingsBody? Settings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NewSettingsDraftBody(ApplicationSettingsBody? Settings);

/// <summary>A reason: required to propose or reject, optional to approve a role type or withdraw.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogReasonBody(string? Reason);

/// <summary>Body of approve: the content hash the approver reviewed (from the review endpoint) and an optional reason.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApproveSettingsBody(string? ContentHash, string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProposeRoleTypeBody(string? Code, string? DisplayName, string? Reason);

/// <summary>Bounds that match the catalog's own rules (ADR-0025); the domain checks every rule again.</summary>
public static class CatalogApiLimits
{
    public const int KeyLength = 64;
    public const int DisplayNameLength = 200;
    public const int RoleTypeDisplayNameLength = 100;
    public const int PersonLength = 200;
    public const int CodeLength = 16;
    public const int MaxRoles = 32;
    public const int ReasonLength = 1000;
    public const int MaxBodyBytes = 32 * 1024;
}

public sealed class ApplicationRoleBodyValidator : AbstractValidator<ApplicationRoleBody>
{
    public ApplicationRoleBodyValidator()
    {
        RuleFor(b => b.RoleTypeCode).NotEmpty().MaximumLength(CatalogApiLimits.KeyLength);
        RuleFor(b => b.Requirement).NotEmpty().MaximumLength(CatalogApiLimits.CodeLength);
        RuleFor(b => b.HolderKind).MaximumLength(CatalogApiLimits.CodeLength);
        RuleFor(b => b.HolderId).MaximumLength(CatalogApiLimits.PersonLength);
    }
}

public sealed class ApplicationSettingsBodyValidator : AbstractValidator<ApplicationSettingsBody>
{
    public ApplicationSettingsBodyValidator()
    {
        RuleFor(b => b.Owner).NotEmpty().MaximumLength(CatalogApiLimits.PersonLength);
        RuleFor(b => b.Roles).Cascade(CascadeMode.Stop).NotNull().Must(r => r!.Count <= CatalogApiLimits.MaxRoles)
            .WithMessage($"At most {CatalogApiLimits.MaxRoles} roles.");
        RuleForEach(b => b.Roles).NotNull().SetValidator(new ApplicationRoleBodyValidator()!);
    }
}

public sealed class RegisterApplicationBodyValidator : AbstractValidator<RegisterApplicationBody>
{
    public RegisterApplicationBodyValidator()
    {
        RuleFor(b => b.Key).NotEmpty().MaximumLength(CatalogApiLimits.KeyLength);
        RuleFor(b => b.DisplayName).NotEmpty().MaximumLength(CatalogApiLimits.DisplayNameLength);
        RuleFor(b => b.Settings).NotNull().SetValidator(new ApplicationSettingsBodyValidator()!);
    }
}

public sealed class RenameApplicationBodyValidator : AbstractValidator<RenameApplicationBody>
{
    public RenameApplicationBodyValidator()
    {
        RuleFor(b => b.ExpectedConcurrencyVersion).NotNull().GreaterThan(0);
        RuleFor(b => b.DisplayName).NotEmpty().MaximumLength(CatalogApiLimits.DisplayNameLength);
    }
}

public sealed class UpdateSettingsDraftBodyValidator : AbstractValidator<UpdateSettingsDraftBody>
{
    public UpdateSettingsDraftBodyValidator()
    {
        RuleFor(b => b.ExpectedConcurrencyVersion).NotNull().GreaterThan(0);
        RuleFor(b => b.Settings).NotNull().SetValidator(new ApplicationSettingsBodyValidator()!);
    }
}

public sealed class NewSettingsDraftBodyValidator : AbstractValidator<NewSettingsDraftBody>
{
    public NewSettingsDraftBodyValidator() => RuleFor(b => b.Settings).NotNull().SetValidator(new ApplicationSettingsBodyValidator()!);
}

public sealed class CatalogReasonBodyValidator : AbstractValidator<CatalogReasonBody>
{
    public CatalogReasonBodyValidator() => RuleFor(b => b.Reason).MaximumLength(CatalogApiLimits.ReasonLength);
}

public sealed class ApproveSettingsBodyValidator : AbstractValidator<ApproveSettingsBody>
{
    public ApproveSettingsBodyValidator()
    {
        RuleFor(b => b.ContentHash).NotEmpty().Length(64).Matches("^[0-9a-f]{64}$");
        RuleFor(b => b.Reason).MaximumLength(CatalogApiLimits.ReasonLength);
    }
}

public sealed class ProposeRoleTypeBodyValidator : AbstractValidator<ProposeRoleTypeBody>
{
    public ProposeRoleTypeBodyValidator()
    {
        RuleFor(b => b.Code).NotEmpty().MaximumLength(CatalogApiLimits.KeyLength);
        RuleFor(b => b.DisplayName).NotEmpty().MaximumLength(CatalogApiLimits.RoleTypeDisplayNameLength);
        RuleFor(b => b.Reason).NotEmpty().MaximumLength(CatalogApiLimits.ReasonLength);
    }
}

/// <summary>Runs the body's validator on every request; field names as the JSON spells them (camelCase).</summary>
internal sealed class CatalogValidationFilter<TBody>(IValidator<TBody> validator, bool reasonRequired = false) : IEndpointFilter
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
        if (reasonRequired && body is CatalogReasonBody { Reason: var reason } && string.IsNullOrWhiteSpace(reason))
        {
            errors["reason"] = ["A reason is required."];
        }

        return errors.Count == 0 ? await next(context).ConfigureAwait(false) : TypedResults.ValidationProblem(errors);
    }
}

/// <summary>
/// Catalog failures as ProblemDetails: a field name and rule, or a refusal code, at most; never stored values, people or
/// reasons. A damaged stored record is a 503.
/// </summary>
internal sealed class CatalogExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var kind = CatalogApi.Classify(exception, out var code, out var field, out var rule);
        if (kind == CatalogFailureKind.None)
        {
            return false;
        }

        if (kind == CatalogFailureKind.InvalidField)
        {
            var name = field is null ? "body" : JsonNamingPolicy.CamelCase.ConvertName(field);
            await Results.ValidationProblem(new Dictionary<string, string[]> { [name] = [rule ?? "is invalid"] }).ExecuteAsync(httpContext).ConfigureAwait(false);
            return true;
        }

        var (status, title) = kind switch
        {
            CatalogFailureKind.Forbidden => (StatusCodes.Status403Forbidden, "The catalog action was refused."),
            CatalogFailureKind.WrongState => (StatusCodes.Status409Conflict, "The catalog action was refused."),
            CatalogFailureKind.Conflict => (StatusCodes.Status409Conflict, "Changed by someone else; reload and try again."),
            CatalogFailureKind.NotFound => (StatusCodes.Status404NotFound, "Not found."),
            CatalogFailureKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "The application catalog is unavailable."),
            _ => (StatusCodes.Status422UnprocessableEntity, "The catalog action was refused."),
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

internal static class ApplicationCatalogEndpoints
{
    public const string ApplicationsPrefix = CatalogModule.RoutePrefix + "/applications";
    public const string RequestsPrefix = CatalogModule.RoutePrefix + "/application-change-requests";
    public const string RoleTypesPrefix = CatalogModule.RoutePrefix + "/application-role-types";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        // Applications.
        var applications = endpoints.MapGroup(ApplicationsPrefix).RequireAuthorization();
        applications.MapGet("/{key}", GetApplicationAsync)
            .RequirePermission(AccessActions.ApplicationsRead)
            .WithName("Catalog.GetApplication");

        var writes = Writes(applications);
        writes.MapPost(string.Empty, RegisterAsync)
            .AddEndpointFilter<CatalogValidationFilter<RegisterApplicationBody>>()
            .RequirePermission(AccessActions.ApplicationsManage)
            .WithName("Catalog.RegisterApplication");

        writes.MapPut("/{id:guid}/name", RenameAsync)
            .AddEndpointFilter<CatalogValidationFilter<RenameApplicationBody>>()
            .RequirePermission(AccessActions.ApplicationsManage)
            .WithName("Catalog.RenameApplication");

        writes.MapPut("/{id:guid}/draft", UpdateDraftAsync)
            .AddEndpointFilter<CatalogValidationFilter<UpdateSettingsDraftBody>>()
            .RequirePermission(AccessActions.ApplicationsManage)
            .WithName("Catalog.UpdateApplicationDraft");

        writes.MapPost("/{id:guid}/drafts", StartNewDraftAsync)
            .AddEndpointFilter<CatalogValidationFilter<NewSettingsDraftBody>>()
            .RequirePermission(AccessActions.ApplicationsManage)
            .WithName("Catalog.StartApplicationDraft");

        writes.MapPost("/{id:guid}/change-requests", ProposeAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.ApplicationsManage)
            .WithName("Catalog.ProposeApplicationChange");

        // Change requests (activation, owner and role changes).
        var requests = endpoints.MapGroup(RequestsPrefix).RequireAuthorization();
        requests.MapGet("/{id:guid}", GetRequestAsync)
            .RequirePermission(AccessActions.ApplicationsRead)
            .WithName("Catalog.GetApplicationChange");

        var decisions = Writes(requests);
        decisions.MapPost("/{id:guid}/approve", ApproveAsync)
            .AddEndpointFilter<CatalogValidationFilter<ApproveSettingsBody>>()
            .RequirePermission(AccessActions.ApplicationsApprove)
            .WithName("Catalog.ApproveApplicationChange");

        decisions.MapPost("/{id:guid}/reject", RejectAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.ApplicationsApprove)
            .WithName("Catalog.RejectApplicationChange");

        decisions.MapPost("/{id:guid}/withdraw", WithdrawAsync)
            .AddEndpointFilter<CatalogValidationFilter<CatalogReasonBody>>()
            .RequirePermission(AccessActions.ApplicationsManage)
            .WithName("Catalog.WithdrawApplicationChange");

        // Role types.
        var roleTypes = endpoints.MapGroup(RoleTypesPrefix).RequireAuthorization();
        roleTypes.MapGet(string.Empty, ListRoleTypesAsync)
            .RequirePermission(AccessActions.ApplicationsRead)
            .WithName("Catalog.ListRoleTypes");

        var typeWrites = Writes(roleTypes);
        typeWrites.MapPost(string.Empty, ProposeRoleTypeAsync)
            .AddEndpointFilter<CatalogValidationFilter<ProposeRoleTypeBody>>()
            .RequirePermission(AccessActions.ApplicationsManage)
            .WithName("Catalog.ProposeRoleType");

        typeWrites.MapPost("/{id:guid}/approve", ApproveRoleTypeAsync)
            .AddEndpointFilter<CatalogValidationFilter<CatalogReasonBody>>()
            .RequirePermission(AccessActions.ApplicationsApprove)
            .WithName("Catalog.ApproveRoleType");

        typeWrites.MapPost("/{id:guid}/reject", RejectRoleTypeAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.ApplicationsApprove)
            .WithName("Catalog.RejectRoleType");

        typeWrites.MapPost("/{id:guid}/withdraw", WithdrawRoleTypeAsync)
            .AddEndpointFilter<CatalogValidationFilter<CatalogReasonBody>>()
            .RequirePermission(AccessActions.ApplicationsManage)
            .WithName("Catalog.WithdrawRoleType");
    }

    private static RouteGroupBuilder Writes(RouteGroupBuilder group) => group.MapGroup(string.Empty)
        .RequireBodyLimit(CatalogApiLimits.MaxBodyBytes)
        .RequireRateLimiting(IamRateLimits.AdministrationWrites);

    private static readonly Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> ReasonRequired = (factory, next) =>
    {
        var filter = new CatalogValidationFilter<CatalogReasonBody>(new CatalogReasonBodyValidator(), reasonRequired: true);
        return context => filter.InvokeAsync(context, next);
    };

    private static async Task<Results<Ok<ApplicationView>, NotFound>> GetApplicationAsync(string key, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken) =>
        await catalog.FindAsync(key, cancellationToken).ConfigureAwait(false) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound();

    private static async Task<Created<ApplicationView>> RegisterAsync(RegisterApplicationBody body, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken)
    {
        var created = await catalog.RegisterAsync(new RegisterApplicationRequest(body.Key!, body.DisplayName!, body.Settings!.ToInput()), cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{ApplicationsPrefix}/{created.Key}", created);
    }

    private static async Task<Ok<ApplicationView>> RenameAsync(Guid id, RenameApplicationBody body, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.RenameAsync(id, body.ExpectedConcurrencyVersion!.Value, body.DisplayName!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApplicationView>> UpdateDraftAsync(Guid id, UpdateSettingsDraftBody body, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.UpdateDraftAsync(id, body.ExpectedConcurrencyVersion!.Value, body.Settings!.ToInput(), cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApplicationView>> StartNewDraftAsync(Guid id, NewSettingsDraftBody body, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.StartNewDraftAsync(id, body.Settings!.ToInput(), cancellationToken).ConfigureAwait(false));

    private static async Task<Created<ApplicationSettingsRequestView>> ProposeAsync(Guid id, CatalogReasonBody body, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken)
    {
        var request = await catalog.ProposeAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{RequestsPrefix}/{request.Id}", request);
    }

    private static async Task<Results<Ok<ApplicationSettingsReview>, NotFound>> GetRequestAsync(Guid id, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken) =>
        await catalog.ReviewRequestAsync(id, cancellationToken).ConfigureAwait(false) is { } review ? TypedResults.Ok(review) : TypedResults.NotFound();

    private static async Task<Ok<ApplicationSettingsRequestView>> ApproveAsync(Guid id, ApproveSettingsBody body, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.ApproveAsync(id, body.ContentHash!, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApplicationSettingsRequestView>> RejectAsync(Guid id, CatalogReasonBody body, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.RejectAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApplicationSettingsRequestView>> WithdrawAsync(Guid id, CatalogReasonBody body, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.WithdrawAsync(id, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<IReadOnlyList<ApplicationRoleTypeView>>> ListRoleTypesAsync(IApplicationCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.ListRoleTypesAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<Created<ApplicationRoleTypeView>> ProposeRoleTypeAsync(ProposeRoleTypeBody body, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken)
    {
        var type = await catalog.ProposeRoleTypeAsync(body.Code!, body.DisplayName!, body.Reason!, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{RoleTypesPrefix}/{type.Id}", type);
    }

    private static async Task<Ok<ApplicationRoleTypeView>> ApproveRoleTypeAsync(Guid id, CatalogReasonBody body, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.ApproveRoleTypeAsync(id, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApplicationRoleTypeView>> RejectRoleTypeAsync(Guid id, CatalogReasonBody body, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.RejectRoleTypeAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ApplicationRoleTypeView>> WithdrawRoleTypeAsync(Guid id, CatalogReasonBody body, IApplicationCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.WithdrawRoleTypeAsync(id, body.Reason, cancellationToken).ConfigureAwait(false));
}
