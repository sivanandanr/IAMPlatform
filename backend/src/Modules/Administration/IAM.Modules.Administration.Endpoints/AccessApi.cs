using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Application.Access;
using IAM.Modules.Administration.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace IAM.Modules.Administration.Endpoints;

/// <summary>
/// Body of <c>POST /api/administration/changes</c>. Only the fields of the chosen kind may be set; any other property in
/// the JSON is refused (mass assignment), and who proposes always comes from the token, never the body.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProposeChangeBody(
    string? Kind,
    string? Reason,
    string? Subject = null,
    string? RoleCode = null,
    string? Code = null,
    string? Name = null,
    IReadOnlyList<string>? Actions = null,
    string? First = null,
    string? Second = null);

/// <summary>Body of approve, reject and withdraw. A reason is required to reject.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DecisionBody(string? Reason);

/// <summary>Shape checks (BACKEND_INPUT_VALIDATION step 6). The domain checks every rule again; these only refuse early and clearly.</summary>
public sealed class ProposeChangeBodyValidator : AbstractValidator<ProposeChangeBody>
{
    public const int MaxActions = 32;

    public ProposeChangeBodyValidator()
    {
        RuleFor(b => b.Kind).NotEmpty().Must(k => AccessChangeKinds.All.Contains(k, StringComparer.Ordinal))
            .WithMessage("Unknown kind.");
        RuleFor(b => b.Reason).NotEmpty().MaximumLength(AccessApiLimits.ReasonLength);

        When(b => b.Kind is AccessChangeKinds.AssignRole or AccessChangeKinds.RemoveAssignment, () =>
        {
            RuleFor(b => b.Subject).NotEmpty().MaximumLength(AccessApiLimits.SubjectLength).Must(NoOuterSpace).WithMessage("Leading or trailing spaces.");
            RuleFor(b => b.RoleCode).NotEmpty().MaximumLength(AccessApiLimits.RoleCodeLength).Matches(AccessApiLimits.RoleCodePattern);
            Only(nameof(ProposeChangeBody.Subject), nameof(ProposeChangeBody.RoleCode));
        });
        When(b => b.Kind is AccessChangeKinds.DefineRole, () =>
        {
            RuleFor(b => b.Code).NotEmpty().MaximumLength(AccessApiLimits.RoleCodeLength).Matches(AccessApiLimits.RoleCodePattern);
            RuleFor(b => b.Name).NotEmpty().MaximumLength(AccessApiLimits.RoleNameLength);
            RuleFor(b => b.Actions).Cascade(CascadeMode.Stop).NotNull().Must(a => a!.Count <= MaxActions).WithMessage($"At most {MaxActions} actions.")
                .Must(a => a!.Distinct(StringComparer.Ordinal).Count() == a!.Count).WithMessage("Duplicate actions.");
            RuleForEach(b => b.Actions).NotEmpty().MaximumLength(AccessApiLimits.ActionLength);
            Only(nameof(ProposeChangeBody.Code), nameof(ProposeChangeBody.Name), nameof(ProposeChangeBody.Actions));
        });
        When(b => b.Kind is AccessChangeKinds.AddIncompatiblePair or AccessChangeKinds.RemoveIncompatiblePair, () =>
        {
            RuleFor(b => b.First).NotEmpty().MaximumLength(AccessApiLimits.RoleCodeLength).Matches(AccessApiLimits.RoleCodePattern);
            RuleFor(b => b.Second).NotEmpty().MaximumLength(AccessApiLimits.RoleCodeLength).Matches(AccessApiLimits.RoleCodePattern);
            Only(nameof(ProposeChangeBody.First), nameof(ProposeChangeBody.Second));
        });
    }

    private static bool NoOuterSpace(string? value) => value is null || value.Trim().Length == value.Length;

    /// <summary>Every change field not listed must be absent for this kind.</summary>
    private void Only(params string[] allowed)
    {
        void Absent<T>(System.Linq.Expressions.Expression<Func<ProposeChangeBody, T>> field, string name)
        {
            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                RuleFor(field).Null().WithMessage("Not allowed for this kind.");
            }
        }

        Absent(b => b.Subject, nameof(ProposeChangeBody.Subject));
        Absent(b => b.RoleCode, nameof(ProposeChangeBody.RoleCode));
        Absent(b => b.Code, nameof(ProposeChangeBody.Code));
        Absent(b => b.Name, nameof(ProposeChangeBody.Name));
        Absent(b => b.Actions, nameof(ProposeChangeBody.Actions));
        Absent(b => b.First, nameof(ProposeChangeBody.First));
        Absent(b => b.Second, nameof(ProposeChangeBody.Second));
    }
}

public sealed class DecisionBodyValidator : AbstractValidator<DecisionBody>
{
    public DecisionBodyValidator() => RuleFor(b => b.Reason).MaximumLength(AccessApiLimits.ReasonLength);
}

/// <summary>Body of reject: a reason is required (the domain requires it too).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RejectBody(string? Reason);

public sealed class RejectBodyValidator : AbstractValidator<RejectBody>
{
    public RejectBodyValidator() => RuleFor(b => b.Reason).NotEmpty().MaximumLength(AccessApiLimits.ReasonLength);
}

/// <summary>Bounds that match the stored columns and the domain (DL-059, ADR-0017).</summary>
public static class AccessApiLimits
{
    public const int SubjectLength = 200;
    public const int RoleCodeLength = 64;
    public const int RoleNameLength = 200;
    public const int ActionLength = 100;
    public const int ReasonLength = 1000;
    public const int MaxBodyBytes = 16 * 1024;
    public const string RoleCodePattern = "^[a-z][a-z0-9-]*$";
}

/// <summary>Runs the body's validator on every request; a body that fails is a 400 with field errors and never reaches the handler.</summary>
internal sealed class ValidationFilter<TBody>(IValidator<TBody> validator) : IEndpointFilter
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

        var result = await validator.ValidateAsync(body, context.HttpContext.RequestAborted).ConfigureAwait(false);
        // Field names as the JSON spells them (camelCase), the same as every other error the API returns.
        return result.IsValid
            ? await next(context).ConfigureAwait(false)
            : TypedResults.ValidationProblem(result.ToDictionary().ToDictionary(e => JsonNamingPolicy.CamelCase.ConvertName(e.Key), e => e.Value, StringComparer.Ordinal));
    }
}

internal static class AccessApi
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/roles", GetRolesAsync)
            .RequirePermission(AccessActions.RolesRead)
            .WithName("Administration.GetRoles");

        group.MapGet("/changes/{id:guid}", GetChangeAsync)
            .RequirePermission(AccessActions.ChangesRead)
            .WithName("Administration.GetChange");

        var writes = group.MapGroup("/changes")
            .RequireBodyLimit(AccessApiLimits.MaxBodyBytes)
            .RequireRateLimiting(IamRateLimits.AdministrationWrites);

        writes.MapPost(string.Empty, ProposeAsync)
            .AddEndpointFilter<ValidationFilter<ProposeChangeBody>>()
            .RequirePermission(AccessActions.ChangesPropose)
            .WithName("Administration.ProposeChange");

        writes.MapPost("/{id:guid}/approve", ApproveAsync)
            .AddEndpointFilter<ValidationFilter<DecisionBody>>()
            .RequirePermission(AccessActions.ChangesApprove)
            .WithName("Administration.ApproveChange");

        writes.MapPost("/{id:guid}/reject", RejectAsync)
            .AddEndpointFilter<ValidationFilter<RejectBody>>()
            .RequirePermission(AccessActions.ChangesApprove)
            .WithName("Administration.RejectChange");

        writes.MapPost("/{id:guid}/withdraw", WithdrawAsync)
            .AddEndpointFilter<ValidationFilter<DecisionBody>>()
            .RequirePermission(AccessActions.ChangesPropose)
            .WithName("Administration.WithdrawChange");
    }

    private static async Task<Ok<RolesView>> GetRolesAsync(IAccessAdministration access, CancellationToken cancellationToken) =>
        TypedResults.Ok(await access.RolesAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<ChangeRequestSummary>, NotFound>> GetChangeAsync(Guid id, IAccessAdministration access, CancellationToken cancellationToken) =>
        await access.FindChangeSummaryAsync(id, cancellationToken).ConfigureAwait(false) is { } change
            ? TypedResults.Ok(change)
            : TypedResults.NotFound();

    private static async Task<Created<ChangeRequestSummary>> ProposeAsync(ProposeChangeBody body, IAccessAdministration access, CancellationToken cancellationToken)
    {
        var input = new AccessChangeInput(body.Kind!, body.Subject, body.RoleCode, body.Code, body.Name, body.Actions, body.First, body.Second);
        var created = await access.ProposeChangeAsync(input, body.Reason!, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"{AdministrationModule.RoutePrefix}/changes/{created.Id}", created);
    }

    private static async Task<Ok<ChangeRequestSummary>> ApproveAsync(Guid id, DecisionBody body, IAccessAdministration access, CancellationToken cancellationToken) =>
        TypedResults.Ok(await access.ApproveChangeAsync(id, body.Reason, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ChangeRequestSummary>> RejectAsync(Guid id, RejectBody body, IAccessAdministration access, CancellationToken cancellationToken) =>
        TypedResults.Ok(await access.RejectChangeAsync(id, body.Reason!, cancellationToken).ConfigureAwait(false));

    private static async Task<Ok<ChangeRequestSummary>> WithdrawAsync(Guid id, DecisionBody body, IAccessAdministration access, CancellationToken cancellationToken) =>
        TypedResults.Ok(await access.WithdrawChangeAsync(id, body.Reason, cancellationToken).ConfigureAwait(false));
}
