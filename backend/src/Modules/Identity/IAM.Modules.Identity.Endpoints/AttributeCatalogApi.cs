using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Identity.Application.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IAM.Modules.Identity.Endpoints;

/// <summary>An attribute draft as the API accepts it. Server-owned fields (id, state, hash, version) are never accepted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AttributeDraftBody(
    string? DisplayName,
    string? DataType,
    string? SubjectScope,
    string? SourceSystem,
    string? Sensitivity,
    string? Normalization,
    int? FreshnessMaxAgeMinutes,
    int? MaxLength,
    IReadOnlyList<string?>? Operators)
{
    public AttributeDraftRequest ToRequest() =>
        new(DisplayName, DataType, SubjectScope, SourceSystem, Sensitivity, Normalization, FreshnessMaxAgeMinutes, MaxLength, Operators);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterAttributeBody(string? Key, AttributeDraftBody? Draft);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateDraftBody(long? ExpectedConcurrencyVersion, AttributeDraftBody? Draft);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NewDraftBody(AttributeDraftBody? Draft);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PublishReasonBody(string? Reason);

/// <summary>Body of approve: the content hash the approver reviewed (from the review endpoint) and an optional reason (DL-065).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApprovePublishBody(string? ContentHash, string? Reason);

/// <summary>A publish request and the draft it would publish (absent once it is no longer a draft), for review.</summary>
public sealed record AttributePublishReviewResponse(AttributePublishRequestView Request, AttributeVersionResponse? Draft);

/// <summary>Bounds that match the catalog's own rules (ADR-0015); the domain checks every rule again.</summary>
public static class AttributeApiLimits
{
    public const int KeyLength = 64;
    public const int DisplayNameLength = 200;
    public const int CodeLength = 64;
    public const int MaxOperators = 16;
    public const int ReasonLength = 1000;
    public const int MaxBodyBytes = 16 * 1024;
}

public sealed class AttributeDraftBodyValidator : AbstractValidator<AttributeDraftBody>
{
    public AttributeDraftBodyValidator()
    {
        RuleFor(b => b.DisplayName).NotEmpty().MaximumLength(AttributeApiLimits.DisplayNameLength);
        RuleFor(b => b.DataType).NotEmpty().MaximumLength(AttributeApiLimits.CodeLength);
        RuleFor(b => b.SubjectScope).NotEmpty().MaximumLength(AttributeApiLimits.CodeLength);
        RuleFor(b => b.SourceSystem).NotEmpty().MaximumLength(AttributeApiLimits.CodeLength);
        RuleFor(b => b.Sensitivity).NotEmpty().MaximumLength(AttributeApiLimits.CodeLength);
        RuleFor(b => b.Normalization).MaximumLength(AttributeApiLimits.CodeLength);
        RuleFor(b => b.FreshnessMaxAgeMinutes).NotNull();
        RuleFor(b => b.Operators).Cascade(CascadeMode.Stop).NotNull().Must(o => o!.Count <= AttributeApiLimits.MaxOperators)
            .WithMessage($"At most {AttributeApiLimits.MaxOperators} operators.");
        RuleForEach(b => b.Operators).NotEmpty().MaximumLength(AttributeApiLimits.CodeLength);
    }
}

public sealed class RegisterAttributeBodyValidator : AbstractValidator<RegisterAttributeBody>
{
    public RegisterAttributeBodyValidator()
    {
        RuleFor(b => b.Key).NotEmpty().MaximumLength(AttributeApiLimits.KeyLength);
        RuleFor(b => b.Draft).NotNull().SetValidator(new AttributeDraftBodyValidator()!);
    }
}

public sealed class UpdateDraftBodyValidator : AbstractValidator<UpdateDraftBody>
{
    public UpdateDraftBodyValidator()
    {
        RuleFor(b => b.ExpectedConcurrencyVersion).NotNull().GreaterThan(0);
        RuleFor(b => b.Draft).NotNull().SetValidator(new AttributeDraftBodyValidator()!);
    }
}

public sealed class NewDraftBodyValidator : AbstractValidator<NewDraftBody>
{
    public NewDraftBodyValidator() => RuleFor(b => b.Draft).NotNull().SetValidator(new AttributeDraftBodyValidator()!);
}

/// <summary>The reason is optional for approve and withdraw and required to propose or reject (checked per endpoint below).</summary>
public sealed class PublishReasonBodyValidator : AbstractValidator<PublishReasonBody>
{
    public PublishReasonBodyValidator() => RuleFor(b => b.Reason).MaximumLength(AttributeApiLimits.ReasonLength);
}

public sealed class ApprovePublishBodyValidator : AbstractValidator<ApprovePublishBody>
{
    public ApprovePublishBodyValidator()
    {
        RuleFor(b => b.ContentHash).NotEmpty().Length(64).Matches("^[0-9a-f]{64}$");
        RuleFor(b => b.Reason).MaximumLength(AttributeApiLimits.ReasonLength);
    }
}

/// <summary>Runs the body's validator on every request; field names as the JSON spells them (camelCase).</summary>
internal sealed class AttributeValidationFilter<TBody>(IValidator<TBody> validator, bool reasonRequired = false) : IEndpointFilter
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
        if (reasonRequired && body is PublishReasonBody { Reason: var reason } && string.IsNullOrWhiteSpace(reason))
        {
            errors["reason"] = ["A reason is required."];
        }

        return errors.Count == 0 ? await next(context).ConfigureAwait(false) : TypedResults.ValidationProblem(errors);
    }
}

/// <summary>
/// Catalog failures as ProblemDetails: a field name and rule, or a refusal code, at most; never stored values, people or
/// reasons. A damaged stored request is a 503.
/// </summary>
internal sealed class AttributeExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var kind = AttributeApi.Classify(exception, out var code, out var field, out var rule);
        if (kind == AttributeFailureKind.None)
        {
            return false;
        }

        if (kind == AttributeFailureKind.InvalidField)
        {
            var name = field is null ? "body" : JsonNamingPolicy.CamelCase.ConvertName(field);
            await Results.ValidationProblem(new Dictionary<string, string[]> { [name] = [rule ?? "is invalid"] }).ExecuteAsync(httpContext).ConfigureAwait(false);
            return true;
        }

        var (status, title) = kind switch
        {
            AttributeFailureKind.Forbidden => (StatusCodes.Status403Forbidden, "The catalog action was refused."),
            AttributeFailureKind.WrongState => (StatusCodes.Status409Conflict, "The catalog action was refused."),
            AttributeFailureKind.Conflict => (StatusCodes.Status409Conflict, "Changed by someone else; reload and try again."),
            AttributeFailureKind.NotFound => (StatusCodes.Status404NotFound, "Not found."),
            AttributeFailureKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "The attribute catalog is unavailable."),
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

internal static class AttributeCatalogApi
{
    public const string PublishRequestsPrefix = "/api/identity/attribute-publish-requests";

    public static void Map(IEndpointRouteBuilder endpoints, RouteGroupBuilder attributes)
    {
        attributes.MapGet("/{key}", GetDraftAsync)
            .RequirePermission(AccessActions.AttributesRead)
            .WithName("Identity.GetAttributeDraft");

        var writes = attributes.MapGroup(string.Empty)
            .RequireBodyLimit(AttributeApiLimits.MaxBodyBytes)
            .RequireRateLimiting(IamRateLimits.AdministrationWrites);

        writes.MapPost(string.Empty, RegisterAsync)
            .AddEndpointFilter<AttributeValidationFilter<RegisterAttributeBody>>()
            .RequirePermission(AccessActions.AttributesManage)
            .WithName("Identity.RegisterAttribute");

        writes.MapPut("/{id:guid}/draft", UpdateDraftAsync)
            .AddEndpointFilter<AttributeValidationFilter<UpdateDraftBody>>()
            .RequirePermission(AccessActions.AttributesManage)
            .WithName("Identity.UpdateAttributeDraft");

        writes.MapPost("/{id:guid}/drafts", StartNewDraftAsync)
            .AddEndpointFilter<AttributeValidationFilter<NewDraftBody>>()
            .RequirePermission(AccessActions.AttributesManage)
            .WithName("Identity.StartAttributeDraft");

        writes.MapPost("/{id:guid}/publish-requests", ProposeAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.AttributesManage)
            .WithName("Identity.ProposeAttributePublish");

        var requests = endpoints.MapGroup(PublishRequestsPrefix).RequireAuthorization();
        requests.MapGet("/{id:guid}", GetRequestAsync)
            .RequirePermission(AccessActions.AttributesRead)
            .WithName("Identity.GetAttributePublishRequest");

        var decisions = requests.MapGroup(string.Empty)
            .RequireBodyLimit(AttributeApiLimits.MaxBodyBytes)
            .RequireRateLimiting(IamRateLimits.AdministrationWrites);

        decisions.MapPost("/{id:guid}/approve", ApproveAsync)
            .AddEndpointFilter<AttributeValidationFilter<ApprovePublishBody>>()
            .RequirePermission(AccessActions.AttributesApprove)
            .WithName("Identity.ApproveAttributePublish");

        decisions.MapPost("/{id:guid}/reject", RejectAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.AttributesApprove)
            .WithName("Identity.RejectAttributePublish");

        decisions.MapPost("/{id:guid}/withdraw", WithdrawAsync)
            .AddEndpointFilter<AttributeValidationFilter<PublishReasonBody>>()
            .RequirePermission(AccessActions.AttributesManage)
            .WithName("Identity.WithdrawAttributePublish");
    }

    private static readonly Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> ReasonRequired = (factory, next) =>
    {
        var filter = new AttributeValidationFilter<PublishReasonBody>(new PublishReasonBodyValidator(), reasonRequired: true);
        return context => filter.InvokeAsync(context, next);
    };

    private static async Task<Results<Ok<AttributeVersionResponse>, NotFound>> GetDraftAsync(string key, IAttributeCatalogAdministration catalog, CancellationToken cancellationToken) =>
        await catalog.FindDraftAsync(key, cancellationToken).ConfigureAwait(false) is { } draft
            ? TypedResults.Ok(AttributeApi.ToResponse(draft))
            : TypedResults.NotFound();

    private static async Task<Created<AttributeVersionResponse>> RegisterAsync(RegisterAttributeBody body, IAttributeCatalogAdministration catalog, CancellationToken cancellationToken)
    {
        var created = await catalog.RegisterAsync(new RegisterAttributeRequest(body.Key, body.Draft!.ToRequest()), cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{IdentityModule.RoutePrefix}/attributes/{created.Key}", AttributeApi.ToResponse(created));
    }

    private static async Task<Ok<AttributeVersionResponse>> UpdateDraftAsync(Guid id, UpdateDraftBody body, IAttributeCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(AttributeApi.ToResponse(await catalog.UpdateDraftAsync(id, body.ExpectedConcurrencyVersion!.Value, body.Draft!.ToRequest(), cancellationToken).ConfigureAwait(false)));

    private static async Task<Ok<AttributeVersionResponse>> StartNewDraftAsync(Guid id, NewDraftBody body, IAttributeCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(AttributeApi.ToResponse(await catalog.StartNewDraftAsync(id, body.Draft!.ToRequest(), cancellationToken).ConfigureAwait(false)));

    private static async Task<Created<AttributePublishRequestView>> ProposeAsync(Guid id, PublishReasonBody body, IAttributeCatalogAdministration catalog, CancellationToken cancellationToken)
    {
        var request = await catalog.ProposePublishAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{PublishRequestsPrefix}/{request.Id}", request);
    }

    private static async Task<Results<Ok<AttributePublishReviewResponse>, NotFound>> GetRequestAsync(Guid id, IAttributeCatalogAdministration catalog, CancellationToken cancellationToken) =>
        await catalog.ReviewPublishRequestAsync(id, cancellationToken).ConfigureAwait(false) is { } review
            ? TypedResults.Ok(new AttributePublishReviewResponse(review.Request, review.Draft is null ? null : AttributeApi.ToResponse(review.Draft)))
            : TypedResults.NotFound();

    private static async Task<Ok<AttributePublishRequestView>> ApproveAsync(Guid id, ApprovePublishBody body, IAttributeCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.ApprovePublishAsync(id, body.ContentHash!, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<AttributePublishRequestView>> RejectAsync(Guid id, PublishReasonBody body, IAttributeCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.RejectPublishAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<AttributePublishRequestView>> WithdrawAsync(Guid id, PublishReasonBody body, IAttributeCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.WithdrawPublishAsync(id, body.Reason, cancellationToken).ConfigureAwait(false));
}
