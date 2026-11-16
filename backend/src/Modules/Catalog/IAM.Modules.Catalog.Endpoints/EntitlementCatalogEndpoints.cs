using System.Text.Json.Serialization;
using FluentValidation;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Catalog.Application.Entitlements;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace IAM.Modules.Catalog.Endpoints;

/// <summary>"Access for" as the API accepts it (spec v3.2 §6.4).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccessForBody(string? Mode, int? Days, int? DefaultDays, int? MaxDays);

/// <summary>
/// An entitlement's governed content as the API accepts it. Server-owned fields (ids, state, hash, acceptance, versions) are
/// never accepted.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EntitlementSettingsBody(
    string? Type,
    string? TechnicalName,
    string? Description,
    string? OwnerKind,
    string? OwnerId,
    string? EscalationWorkgroup,
    bool? Privileged,
    string? Risk,
    IReadOnlyList<string?>? AllowedAccountClasses,
    AccessForBody? AccessFor)
{
    public EntitlementSettingsText ToText() => new(
        Type, TechnicalName, Description, OwnerKind, OwnerId, EscalationWorkgroup, Privileged, Risk, AllowedAccountClasses,
        AccessFor?.Mode, AccessFor?.Days, AccessFor?.DefaultDays, AccessFor?.MaxDays);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterEntitlementBody(string? Key, string? DisplayName, EntitlementSettingsBody? Settings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RenameEntitlementBody(long? ExpectedConcurrencyVersion, string? DisplayName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateEntitlementDraftBody(long? ExpectedConcurrencyVersion, EntitlementSettingsBody? Settings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NewEntitlementDraftBody(EntitlementSettingsBody? Settings);

/// <summary>Body of accept-ownership: the content hash of the draft the owner was shown.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptOwnershipBody(string? ContentHash);

public static class EntitlementApiLimits
{
    public const int TechnicalNameLength = 256;
    public const int DescriptionLength = 2000;
    public const int MaxAccountClasses = 4;
    public const int MaxDays = 3650;
}

public sealed class AccessForBodyValidator : AbstractValidator<AccessForBody>
{
    public AccessForBodyValidator()
    {
        RuleFor(b => b.Mode).NotEmpty().MaximumLength(32);
        RuleFor(b => b.Days).InclusiveBetween(1, EntitlementApiLimits.MaxDays);
        RuleFor(b => b.DefaultDays).InclusiveBetween(1, EntitlementApiLimits.MaxDays);
        RuleFor(b => b.MaxDays).InclusiveBetween(1, EntitlementApiLimits.MaxDays);
    }
}

public sealed class EntitlementSettingsBodyValidator : AbstractValidator<EntitlementSettingsBody>
{
    public EntitlementSettingsBodyValidator()
    {
        RuleFor(b => b.Type).NotEmpty().MaximumLength(32);
        RuleFor(b => b.TechnicalName).NotEmpty().MaximumLength(EntitlementApiLimits.TechnicalNameLength);
        RuleFor(b => b.Description).NotEmpty().MaximumLength(EntitlementApiLimits.DescriptionLength);
        RuleFor(b => b.OwnerKind).NotEmpty().MaximumLength(CatalogApiLimits.CodeLength);
        RuleFor(b => b.OwnerId).NotEmpty().MaximumLength(CatalogApiLimits.PersonLength);
        RuleFor(b => b.EscalationWorkgroup).NotEmpty().MaximumLength(CatalogApiLimits.KeyLength);
        RuleFor(b => b.Privileged).NotNull();
        RuleFor(b => b.Risk).NotEmpty().MaximumLength(CatalogApiLimits.CodeLength);
        RuleFor(b => b.AllowedAccountClasses).Cascade(CascadeMode.Stop).NotNull().NotEmpty()
            .Must(c => c!.Count <= EntitlementApiLimits.MaxAccountClasses).WithMessage($"At most {EntitlementApiLimits.MaxAccountClasses} account types.");
        RuleForEach(b => b.AllowedAccountClasses).NotEmpty().MaximumLength(CatalogApiLimits.CodeLength);
        RuleFor(b => b.AccessFor).NotNull().SetValidator(new AccessForBodyValidator()!);
    }
}

public sealed class RegisterEntitlementBodyValidator : AbstractValidator<RegisterEntitlementBody>
{
    public RegisterEntitlementBodyValidator()
    {
        RuleFor(b => b.Key).NotEmpty().MaximumLength(CatalogApiLimits.KeyLength);
        RuleFor(b => b.DisplayName).NotEmpty().MaximumLength(CatalogApiLimits.DisplayNameLength);
        RuleFor(b => b.Settings).NotNull().SetValidator(new EntitlementSettingsBodyValidator()!);
    }
}

public sealed class RenameEntitlementBodyValidator : AbstractValidator<RenameEntitlementBody>
{
    public RenameEntitlementBodyValidator()
    {
        RuleFor(b => b.ExpectedConcurrencyVersion).NotNull().GreaterThan(0);
        RuleFor(b => b.DisplayName).NotEmpty().MaximumLength(CatalogApiLimits.DisplayNameLength);
    }
}

public sealed class UpdateEntitlementDraftBodyValidator : AbstractValidator<UpdateEntitlementDraftBody>
{
    public UpdateEntitlementDraftBodyValidator()
    {
        RuleFor(b => b.ExpectedConcurrencyVersion).NotNull().GreaterThan(0);
        RuleFor(b => b.Settings).NotNull().SetValidator(new EntitlementSettingsBodyValidator()!);
    }
}

public sealed class NewEntitlementDraftBodyValidator : AbstractValidator<NewEntitlementDraftBody>
{
    public NewEntitlementDraftBodyValidator() => RuleFor(b => b.Settings).NotNull().SetValidator(new EntitlementSettingsBodyValidator()!);
}

public sealed class AcceptOwnershipBodyValidator : AbstractValidator<AcceptOwnershipBody>
{
    public AcceptOwnershipBodyValidator() => RuleFor(b => b.ContentHash).NotEmpty().Length(64).Matches("^[0-9a-f]{64}$");
}

/// <summary>
/// Entitlement catalog endpoints (DL-087). Reads and writes name a catalog action; accepting ownership is open to anyone signed
/// in, and the service checks that the caller is the owner named in the draft.
/// </summary>
internal static class EntitlementCatalogEndpoints
{
    public const string EntitlementsPrefix = CatalogModule.RoutePrefix + "/entitlements";
    public const string RequestsPrefix = CatalogModule.RoutePrefix + "/entitlement-change-requests";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var byApplication = endpoints.MapGroup(ApplicationCatalogEndpoints.ApplicationsPrefix).RequireAuthorization();
        byApplication.MapGet("/{applicationKey}/entitlements", ListAsync)
            .RequirePermission(AccessActions.EntitlementsRead)
            .WithName("Catalog.ListEntitlements");
        byApplication.MapGet("/{applicationKey}/entitlements/{key}", GetAsync)
            .RequirePermission(AccessActions.EntitlementsRead)
            .WithName("Catalog.GetEntitlement");
        Writes(byApplication).MapPost("/{applicationId:guid}/entitlements", RegisterAsync)
            .AddEndpointFilter<CatalogValidationFilter<RegisterEntitlementBody>>()
            .RequirePermission(AccessActions.EntitlementsManage)
            .WithName("Catalog.RegisterEntitlement");

        var entitlements = Writes(endpoints.MapGroup(EntitlementsPrefix).RequireAuthorization());
        entitlements.MapPut("/{id:guid}/name", RenameAsync)
            .AddEndpointFilter<CatalogValidationFilter<RenameEntitlementBody>>()
            .RequirePermission(AccessActions.EntitlementsManage)
            .WithName("Catalog.RenameEntitlement");
        entitlements.MapPut("/{id:guid}/draft", UpdateDraftAsync)
            .AddEndpointFilter<CatalogValidationFilter<UpdateEntitlementDraftBody>>()
            .RequirePermission(AccessActions.EntitlementsManage)
            .WithName("Catalog.UpdateEntitlementDraft");
        entitlements.MapPost("/{id:guid}/drafts", StartNewDraftAsync)
            .AddEndpointFilter<CatalogValidationFilter<NewEntitlementDraftBody>>()
            .RequirePermission(AccessActions.EntitlementsManage)
            .WithName("Catalog.StartEntitlementDraft");
        entitlements.MapPost("/{id:guid}/ownership/accept", AcceptOwnershipAsync)
            .AddEndpointFilter<CatalogValidationFilter<AcceptOwnershipBody>>()
            .RequirePermission(AccessActions.EntitlementOwnershipAccept)
            .WithName("Catalog.AcceptEntitlementOwnership");
        entitlements.MapPost("/{id:guid}/change-requests", ProposeAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.EntitlementsManage)
            .WithName("Catalog.ProposeEntitlementChange");

        var requests = endpoints.MapGroup(RequestsPrefix).RequireAuthorization();
        requests.MapGet("/{id:guid}", GetRequestAsync)
            .RequirePermission(AccessActions.EntitlementsRead)
            .WithName("Catalog.GetEntitlementChange");
        var decisions = Writes(requests);
        decisions.MapPost("/{id:guid}/approve", ApproveAsync)
            .AddEndpointFilter<CatalogValidationFilter<ApproveSettingsBody>>()
            .RequirePermission(AccessActions.EntitlementsApprove)
            .WithName("Catalog.ApproveEntitlementChange");
        decisions.MapPost("/{id:guid}/reject", RejectAsync)
            .AddEndpointFilterFactory(ReasonRequired)
            .RequirePermission(AccessActions.EntitlementsApprove)
            .WithName("Catalog.RejectEntitlementChange");
        decisions.MapPost("/{id:guid}/withdraw", WithdrawAsync)
            .AddEndpointFilter<CatalogValidationFilter<CatalogReasonBody>>()
            .RequirePermission(AccessActions.EntitlementsManage)
            .WithName("Catalog.WithdrawEntitlementChange");
    }

    private static RouteGroupBuilder Writes(RouteGroupBuilder group) => group.MapGroup(string.Empty)
        .RequireBodyLimit(CatalogApiLimits.MaxBodyBytes)
        .RequireRateLimiting(IamRateLimits.AdministrationWrites);

    private static readonly Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> ReasonRequired = (factory, next) =>
    {
        var filter = new CatalogValidationFilter<CatalogReasonBody>(new CatalogReasonBodyValidator(), reasonRequired: true);
        return context => filter.InvokeAsync(context, next);
    };

    private static async Task<Results<Ok<IReadOnlyList<EntitlementSummary>>, NotFound>> ListAsync(string applicationKey, IEntitlementCatalogAdministration catalog, CancellationToken cancellationToken) =>
        await catalog.ListAsync(applicationKey, cancellationToken).ConfigureAwait(false) is { } list ? TypedResults.Ok(list) : TypedResults.NotFound();

    private static async Task<Results<Ok<EntitlementView>, NotFound>> GetAsync(string applicationKey, string key, IEntitlementCatalogAdministration catalog, CancellationToken cancellationToken) =>
        await catalog.FindAsync(applicationKey, key, cancellationToken).ConfigureAwait(false) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound();

    private static async Task<Created<EntitlementView>> RegisterAsync(Guid applicationId, RegisterEntitlementBody body, IEntitlementCatalogAdministration catalog, CancellationToken cancellationToken)
    {
        var created = await catalog.RegisterAsync(applicationId, body.Key!, body.DisplayName!, body.Settings!.ToText(), cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{ApplicationCatalogEndpoints.ApplicationsPrefix}/{created.ApplicationKey}/entitlements/{created.Key}", created);
    }

    private static async Task<Ok<EntitlementView>> RenameAsync(Guid id, RenameEntitlementBody body, IEntitlementCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.RenameAsync(id, body.ExpectedConcurrencyVersion!.Value, body.DisplayName!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<EntitlementView>> UpdateDraftAsync(Guid id, UpdateEntitlementDraftBody body, IEntitlementCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.UpdateDraftAsync(id, body.ExpectedConcurrencyVersion!.Value, body.Settings!.ToText(), cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<EntitlementView>> StartNewDraftAsync(Guid id, NewEntitlementDraftBody body, IEntitlementCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.StartNewDraftAsync(id, body.Settings!.ToText(), cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<EntitlementView>> AcceptOwnershipAsync(Guid id, AcceptOwnershipBody body, IEntitlementCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.AcceptOwnershipAsync(id, body.ContentHash!, cancellationToken).ConfigureAwait(false));

    private static async Task<Created<EntitlementChangeRequestView>> ProposeAsync(Guid id, CatalogReasonBody body, IEntitlementCatalogAdministration catalog, CancellationToken cancellationToken)
    {
        var request = await catalog.ProposeAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{RequestsPrefix}/{request.Id}", request);
    }

    private static async Task<Results<Ok<EntitlementChangeReview>, NotFound>> GetRequestAsync(Guid id, IEntitlementCatalogAdministration catalog, CancellationToken cancellationToken) =>
        await catalog.ReviewRequestAsync(id, cancellationToken).ConfigureAwait(false) is { } review ? TypedResults.Ok(review) : TypedResults.NotFound();

    private static async Task<Ok<EntitlementChangeRequestView>> ApproveAsync(Guid id, ApproveSettingsBody body, IEntitlementCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.ApproveAsync(id, body.ContentHash!, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<EntitlementChangeRequestView>> RejectAsync(Guid id, CatalogReasonBody body, IEntitlementCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.RejectAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<EntitlementChangeRequestView>> WithdrawAsync(Guid id, CatalogReasonBody body, IEntitlementCatalogAdministration catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(await catalog.WithdrawAsync(id, body.Reason, cancellationToken).ConfigureAwait(false));
}
