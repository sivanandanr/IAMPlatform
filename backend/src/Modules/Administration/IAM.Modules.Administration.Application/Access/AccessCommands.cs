using IAM.Modules.Administration.Domain.Access;

namespace IAM.Modules.Administration.Application.Access;

/// <summary>Change kinds as the API names them (exact, case-sensitive).</summary>
public static class AccessChangeKinds
{
    public const string AssignRole = "AssignRole";
    public const string RemoveAssignment = "RemoveAssignment";
    public const string DefineRole = "DefineRole";
    public const string AddIncompatiblePair = "AddIncompatiblePair";
    public const string RemoveIncompatiblePair = "RemoveIncompatiblePair";

    public static IReadOnlyList<string> All { get; } = [AssignRole, RemoveAssignment, DefineRole, AddIncompatiblePair, RemoveIncompatiblePair];
}

/// <summary>
/// A proposed change as a caller sends it: a kind and only that kind's fields. Who proposes is never part of it; it comes
/// from the server-side actor.
/// </summary>
public sealed record AccessChangeInput(
    string Kind,
    string? Subject = null,
    string? RoleCode = null,
    string? Code = null,
    string? Name = null,
    IReadOnlyList<string>? Actions = null,
    string? First = null,
    string? Second = null);

/// <summary>A change request as callers see it: plain values, no domain types.</summary>
public sealed record ChangeRequestSummary(
    Guid Id,
    AccessChangeInput Change,
    string Maker,
    string Reason,
    DateTimeOffset ProposedAtUtc,
    long ProposedAgainstVersion,
    string State,
    string? Checker,
    string? DecisionReason,
    DateTimeOffset? DecidedAtUtc,
    long? AppliedAsVersion);

public sealed record RoleSummary(string Code, string Name, bool Assignable, IReadOnlyList<string> Actions);

public sealed record IncompatiblePairSummary(string First, string Second);

/// <summary>Roles, what they allow and which can't be held together. Not who holds them.</summary>
public sealed record RolesView(IReadOnlyList<RoleSummary> Roles, IReadOnlyList<IncompatiblePairSummary> IncompatiblePairs, long ModelVersion);

/// <summary>The operations the admin API and the bootstrap tool use, in plain values (P0-013 slices 4-5).</summary>
public static class AccessCommands
{
    /// <summary>Proposes a change under a new server-generated id. A kind that isn't known is refused as an invalid change.</summary>
    public static async Task<ChangeRequestSummary> ProposeChangeAsync(this IAccessAdministration access, AccessChangeInput input, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var view = await access.ProposeAsync(Guid.CreateVersion7(), ToChange(input), reason, cancellationToken).ConfigureAwait(false);
        return Summarize(view);
    }

    public static async Task<ChangeRequestSummary> ApproveChangeAsync(this IAccessAdministration access, Guid id, string? reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        return Summarize(await access.ApproveAsync(id, reason, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<ChangeRequestSummary> RejectChangeAsync(this IAccessAdministration access, Guid id, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        return Summarize(await access.RejectAsync(id, reason, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<ChangeRequestSummary> WithdrawChangeAsync(this IAccessAdministration access, Guid id, string? reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        return Summarize(await access.WithdrawAsync(id, reason, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<ChangeRequestSummary?> FindChangeSummaryAsync(this IAccessAdministration access, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var view = await access.FindChangeAsync(id, cancellationToken).ConfigureAwait(false);
        return view is null ? null : Summarize(view);
    }

    public static async Task<RolesView> RolesAsync(this IAccessAdministration access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var model = await access.CurrentModelAsync(cancellationToken).ConfigureAwait(false);
        return new RolesView(
            [.. model.Roles.Select(r => new RoleSummary(r.Code, r.Name, r.Assignable, [.. r.Actions.Order(StringComparer.Ordinal)]))],
            [.. model.IncompatiblePairs.Select(p => new IncompatiblePairSummary(p.First, p.Second))],
            model.Version);
    }

    /// <summary>Only the kind's own fields are used; the API validator has already refused any others.</summary>
    internal static AccessChange ToChange(AccessChangeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Kind switch
        {
            AccessChangeKinds.AssignRole => new AssignRole(input.Subject!, input.RoleCode!),
            AccessChangeKinds.RemoveAssignment => new RemoveAssignment(input.Subject!, input.RoleCode!),
            AccessChangeKinds.DefineRole => new DefineRole(input.Code!, input.Name!, input.Actions ?? []),
            AccessChangeKinds.AddIncompatiblePair => new AddIncompatiblePair(input.First!, input.Second!),
            AccessChangeKinds.RemoveIncompatiblePair => new RemoveIncompatiblePair(input.First!, input.Second!),
            _ => throw new AccessRefusedException(AccessRefusal.InvalidChange),
        };
    }

    internal static ChangeRequestSummary Summarize(ChangeRequestView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var change = view.Change switch
        {
            AssignRole c => new AccessChangeInput(AccessChangeKinds.AssignRole, Subject: c.Subject, RoleCode: c.RoleCode),
            RemoveAssignment c => new AccessChangeInput(AccessChangeKinds.RemoveAssignment, Subject: c.Subject, RoleCode: c.RoleCode),
            DefineRole c => new AccessChangeInput(AccessChangeKinds.DefineRole, Code: c.Code, Name: c.Name, Actions: [.. c.Actions]),
            AddIncompatiblePair c => new AccessChangeInput(AccessChangeKinds.AddIncompatiblePair, First: c.First, Second: c.Second),
            RemoveIncompatiblePair c => new AccessChangeInput(AccessChangeKinds.RemoveIncompatiblePair, First: c.First, Second: c.Second),
            _ => throw new AccessStoreIntegrityException("change", "A stored access change has an unknown kind."),
        };
        return new ChangeRequestSummary(
            view.Id, change, view.Maker, view.Reason, view.ProposedAtUtc, view.ProposedAgainstVersion, view.State.ToString(),
            view.Checker, view.DecisionReason, view.DecidedAtUtc, view.AppliedAsVersion);
    }
}
