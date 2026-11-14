using IAM.BuildingBlocks.Abstractions.People;
using IAM.Modules.Catalog.Contracts;
using IAM.Modules.Organization.Contracts;
using IAM.Modules.Workflow.Domain.Approvals;

namespace IAM.Modules.Workflow.Application.Approvers;

/// <summary>Where unresolved levels go (DL-057b, DL-068c). The key is checked when the settings are made, so a malformed one fails on first use, never silently.</summary>
public sealed class ApproverResolutionSettings
{
    public const string DefaultFallbackWorkgroup = "iam_managers";

    public ApproverResolutionSettings(string? fallbackWorkgroup = null)
    {
        var key = string.IsNullOrEmpty(fallbackWorkgroup) ? DefaultFallbackWorkgroup : fallbackWorkgroup;
        if (!ApproverSource.IsWorkgroupKey(key))
        {
            throw new ArgumentException("Workflow:FallbackWorkgroup must be a workgroup key.", nameof(fallbackWorkgroup));
        }

        FallbackWorkgroup = key;
    }

    public string FallbackWorkgroup { get; }
}

/// <summary>Finds a level's approvers when it opens (DL-068, DL-069).</summary>
public interface IApproverResolver
{
    /// <summary>
    /// Resolves <paramref name="source"/>, leaving out <paramref name="excluded"/> (the requester, the person getting
    /// access and the policy maker, AR-2.7). Never throws for missing data: anything missing is a step in the evidence,
    /// and if nobody is left the outcome holds the level.
    /// </summary>
    Task<ApproverResolution> ResolveAsync(ApproverSource source, IReadOnlySet<PersonId> excluded, CancellationToken cancellationToken);
}

/// <summary>
/// The resolution chain (DL-068d, DL-069c): the level's own source; for an application role that is empty and optional,
/// or whose holders are all excluded, the application owner; then, for any source that left nobody, the fallback
/// workgroup. A required role that is empty holds (DL-052). Manager, manager of manager, entitlement owner and matrix
/// have no data yet and hold with no fallback (DL-069d). Only active catalog settings and active member lists are read.
/// </summary>
public sealed class ApproverResolver(IApplicationApproverDirectory applications, IWorkgroupDirectory workgroups, ApproverResolutionSettings settings) : IApproverResolver
{
    public async Task<ApproverResolution> ResolveAsync(ApproverSource source, IReadOnlySet<PersonId> excluded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(excluded);
        var steps = new List<ResolutionStep>();

        switch (source.Kind)
        {
            case ApproverSourceKind.NamedUsers:
                if (Take(steps, Step(ApproverSourceKind.NamedUsers, null, null, null, source.People, excluded)) is { } named)
                {
                    return Resolved(named, steps);
                }

                break;

            case ApproverSourceKind.Workgroup:
                if (Take(steps, await WorkgroupStepAsync(ApproverSourceKind.Workgroup, source.WorkgroupKey, excluded, cancellationToken).ConfigureAwait(false)) is { } members)
                {
                    return Resolved(members, steps);
                }

                break;

            case ApproverSourceKind.ApplicationOwner:
                {
                    var application = await FindApplicationAsync(source.ApplicationId, cancellationToken).ConfigureAwait(false);
                    if (Take(steps, OwnerStep(application, source.ApplicationId, excluded)) is { } owner)
                    {
                        return Resolved(owner, steps);
                    }

                    break;
                }

            case ApproverSourceKind.ApplicationRole:
                {
                    var application = await FindApplicationAsync(source.ApplicationId, cancellationToken).ConfigureAwait(false);
                    var role = application?.Roles.FirstOrDefault(r => r.RoleTypeId == source.RoleTypeId);
                    var (roleSteps, holders) = await RoleStepsAsync(application, role, source, excluded, cancellationToken).ConfigureAwait(false);
                    steps.AddRange(roleSteps);
                    var roleStep = roleSteps[0];
                    if (roleStep.Result == ResolutionStepResult.Found)
                    {
                        return Resolved(holders, steps);
                    }

                    // An empty required role blocks; it is never filled by the owner or the fallback group (DL-052).
                    if (role is { Requirement: ApplicationRoleRequirement.Required } && roleStep.Result == ResolutionStepResult.Empty)
                    {
                        return new ApproverResolution(ResolutionOutcome.RequiredRoleEmpty, [], steps);
                    }

                    if (application is not null && Take(steps, OwnerStep(application, source.ApplicationId, excluded)) is { } owner)
                    {
                        return Resolved(owner, steps);
                    }

                    break;
                }

            default:
                // No data yet: hold, and never stand IAM managers in for a line manager or an entitlement owner (DL-069d).
                steps.Add(new ResolutionStep(source.Kind, ResolutionStepResult.Unavailable, null, null, null, 0, 0));
                return new ApproverResolution(ResolutionOutcome.SourceUnavailable, [], steps);
        }

        // Every source that left nobody goes to the fallback workgroup, which is checked the same way (DL-057b).
        if (!(source.Kind == ApproverSourceKind.Workgroup && string.Equals(source.WorkgroupKey, settings.FallbackWorkgroup, StringComparison.Ordinal))
            && Take(steps, await WorkgroupStepAsync(ApproverSourceKind.FallbackWorkgroup, settings.FallbackWorkgroup, excluded, cancellationToken).ConfigureAwait(false)) is { } fallback)
        {
            return Resolved(fallback, steps);
        }

        return new ApproverResolution(ResolutionOutcome.NobodyAvailable, [], steps);
    }

    private static ApproverResolution Resolved(IReadOnlyList<PersonId> people, List<ResolutionStep> steps) =>
        new(ResolutionOutcome.Resolved, people, steps);

    /// <summary>Records the step, and returns its people if it found anyone who may act.</summary>
    private static IReadOnlyList<PersonId>? Take(List<ResolutionStep> steps, (ResolutionStep Step, IReadOnlyList<PersonId> People) found)
    {
        steps.Add(found.Step);
        return found.Step.Result == ResolutionStepResult.Found ? found.People : null;
    }

    private Task<ApplicationApprovers?> FindApplicationAsync(Guid? applicationId, CancellationToken cancellationToken) =>
        applicationId is { } id && id != Guid.Empty ? applications.FindActiveAsync(id, cancellationToken) : Task.FromResult<ApplicationApprovers?>(null);

    private static (ResolutionStep, IReadOnlyList<PersonId>) OwnerStep(ApplicationApprovers? application, Guid? applicationId, IReadOnlySet<PersonId> excluded)
    {
        var reference = applicationId?.ToString("D");
        if (application is null || !PersonId.TryCreate(application.Owner, out var owner))
        {
            return (NotFound(ApproverSourceKind.ApplicationOwner, reference), []);
        }

        return Step(ApproverSourceKind.ApplicationOwner, reference, application.VersionNumber, application.ContentHash, [owner], excluded);
    }

    /// <summary>The role's step first; for a workgroup holder, the workgroup's own step after it, with its version and hash.</summary>
    private async Task<(IReadOnlyList<ResolutionStep>, IReadOnlyList<PersonId>)> RoleStepsAsync(
        ApplicationApprovers? application,
        ApplicationRoleHolding? role,
        ApproverSource source,
        IReadOnlySet<PersonId> excluded,
        CancellationToken cancellationToken)
    {
        var reference = $"{source.ApplicationId:D}:{source.RoleTypeId:D}";
        if (application is null || role is null)
        {
            return ([NotFound(ApproverSourceKind.ApplicationRole, reference)], []);
        }

        switch (role.HolderKind)
        {
            case null:
                return ([new ResolutionStep(ApproverSourceKind.ApplicationRole, ResolutionStepResult.Empty, reference, application.VersionNumber, application.ContentHash, 0, 0)], []);

            case ApplicationRoleHolderKind.Person when PersonId.TryCreate(role.HolderId, out var person):
                {
                    var (step, people) = Step(ApproverSourceKind.ApplicationRole, reference, application.VersionNumber, application.ContentHash, [person], excluded);
                    return ([step], people);
                }

            case ApplicationRoleHolderKind.Group:
                {
                    // The role step carries the catalog version and the workgroup's result; the workgroup step its own version.
                    var (groupStep, members) = await WorkgroupStepAsync(ApproverSourceKind.Workgroup, role.HolderId, excluded, cancellationToken).ConfigureAwait(false);
                    var roleStep = groupStep with { Source = ApproverSourceKind.ApplicationRole, Reference = reference, Version = application.VersionNumber, Hash = application.ContentHash };
                    return ([roleStep, groupStep], groupStep.Result == ResolutionStepResult.Found ? members : []);
                }

            default:
                return ([NotFound(ApproverSourceKind.ApplicationRole, reference)], []);
        }
    }

    private async Task<(ResolutionStep, IReadOnlyList<PersonId>)> WorkgroupStepAsync(ApproverSourceKind kind, string? key, IReadOnlySet<PersonId> excluded, CancellationToken cancellationToken)
    {
        if (!ApproverSource.IsWorkgroupKey(key))
        {
            return (NotFound(kind, null), []);
        }

        var membership = await workgroups.FindActiveAsync(key, cancellationToken).ConfigureAwait(false);
        if (membership is null)
        {
            return (NotFound(kind, key), []);
        }

        // A stored member that isn't a valid id is damage: use nobody from this list rather than part of it.
        var people = new List<PersonId>(membership.Members.Count);
        foreach (var member in membership.Members)
        {
            if (!PersonId.TryCreate(member, out var person))
            {
                return (NotFound(kind, key), []);
            }

            people.Add(person);
        }

        return Step(kind, key, membership.VersionNumber, membership.ContentHash, people, excluded);
    }

    private static (ResolutionStep, IReadOnlyList<PersonId>) Step(ApproverSourceKind kind, string? reference, int? version, string? hash, IReadOnlyList<PersonId> found, IReadOnlySet<PersonId> excluded)
    {
        PersonId[] people = [.. found.Distinct().Where(p => p.IsValid && !excluded.Contains(p))];
        var result = found.Count == 0 ? ResolutionStepResult.Empty : people.Length == 0 ? ResolutionStepResult.AllExcluded : ResolutionStepResult.Found;
        return (new ResolutionStep(kind, result, reference, version, hash, found.Count, found.Count - people.Length), people);
    }

    private static ResolutionStep NotFound(ApproverSourceKind kind, string? reference) =>
        new(kind, ResolutionStepResult.NotFound, reference, null, null, 0, 0);
}
