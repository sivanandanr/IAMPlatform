using System.Diagnostics.CodeAnalysis;
using IAM.BuildingBlocks.Abstractions.People;

namespace IAM.Modules.Workflow.Domain.Approvals;

/// <summary>Where a level's approvers come from (spec 1.8, IAM-WF-003, AR-2.5). They are found when the level opens (DL-068a, DL-069b).</summary>
public enum ApproverSourceKind
{
    /// <summary>People named in the route.</summary>
    NamedUsers = 1,

    /// <summary>The active members of an IAM workgroup (IAM-WF-011).</summary>
    Workgroup = 2,

    /// <summary>The application's owner, from its active catalog settings.</summary>
    ApplicationOwner = 3,

    /// <summary>The holder of an application role, from the application's active catalog settings (AR-2.5).</summary>
    ApplicationRole = 4,

    // The next four have no data yet, so they always answer "unavailable" and the level holds (DL-068a, DL-069d).
    Manager = 5,
    ManagerOfManager = 6,
    EntitlementOwner = 7,
    Matrix = 8,

    /// <summary>The configured fallback workgroup (DL-057b, DL-068c). Only a resolution step, never a route's source.</summary>
    FallbackWorkgroup = 9,
}

/// <summary>
/// A level's approver source as configured. Exactly the fields its kind needs are set: people for named users, a key for a
/// workgroup, an application for the owner, an application and role type for a role, and nothing for the others.
/// </summary>
public sealed record ApproverSource(
    ApproverSourceKind Kind,
    IReadOnlyList<PersonId> People,
    string? WorkgroupKey = null,
    Guid? ApplicationId = null,
    Guid? RoleTypeId = null)
{
    public const int WorkgroupKeyMaxLength = 64;

    public static ApproverSource Named(params PersonId[] people) => new(ApproverSourceKind.NamedUsers, people);

    public static ApproverSource Workgroup(string key) => new(ApproverSourceKind.Workgroup, [], WorkgroupKey: key);

    public static ApproverSource ApplicationOwner(Guid applicationId) => new(ApproverSourceKind.ApplicationOwner, [], ApplicationId: applicationId);

    public static ApproverSource ApplicationRole(Guid applicationId, Guid roleTypeId) =>
        new(ApproverSourceKind.ApplicationRole, [], ApplicationId: applicationId, RoleTypeId: roleTypeId);

    /// <summary>A source whose data doesn't exist yet: Manager, ManagerOfManager, EntitlementOwner or Matrix.</summary>
    public static ApproverSource Unbuilt(ApproverSourceKind kind) => new(kind, []);

    /// <summary>The kind only: logging this object must not expose a person.</summary>
    public override string ToString() => $"{nameof(ApproverSource)} {Kind}";

    /// <summary>Checks the fields match the kind. People are checked as candidates are: 1 to 256, valid and distinct.</summary>
    internal bool IsValid() => Kind switch
    {
        ApproverSourceKind.NamedUsers => ApprovalRoute.CandidatesAreValid(People) && WorkgroupKey is null && ApplicationId is null && RoleTypeId is null,
        ApproverSourceKind.Workgroup => People is { Count: 0 } && IsWorkgroupKey(WorkgroupKey) && ApplicationId is null && RoleTypeId is null,
        ApproverSourceKind.ApplicationOwner => People is { Count: 0 } && WorkgroupKey is null && IsId(ApplicationId) && RoleTypeId is null,
        ApproverSourceKind.ApplicationRole => People is { Count: 0 } && WorkgroupKey is null && IsId(ApplicationId) && IsId(RoleTypeId),
        ApproverSourceKind.Manager or ApproverSourceKind.ManagerOfManager or ApproverSourceKind.EntitlementOwner or ApproverSourceKind.Matrix =>
            People is { Count: 0 } && WorkgroupKey is null && ApplicationId is null && RoleTypeId is null,
        _ => false,
    };

    /// <summary>The workgroup key shape (DL-068): 2-64 lower-case letters, digits or underscores, starting with a letter.</summary>
    public static bool IsWorkgroupKey([NotNullWhen(true)] string? key) =>
        key is { Length: >= 2 and <= WorkgroupKeyMaxLength } && key[0] is >= 'a' and <= 'z' && key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool IsId(Guid? id) => id is { } value && value != Guid.Empty;
}

/// <summary>How resolution ended. Only <see cref="Resolved"/> gives a level people; every other outcome holds it.</summary>
public enum ResolutionOutcome
{
    Resolved = 1,

    /// <summary>The source has no data yet (DL-069d). No fallback: IAM managers never stand in for a line manager or an entitlement owner.</summary>
    SourceUnavailable = 2,

    /// <summary>Every step of the chain was empty, missing or had only excluded people (DL-069c).</summary>
    NobodyAvailable = 3,

    /// <summary>A required application role was empty when the level opened (DL-052).</summary>
    RequiredRoleEmpty = 4,
}

/// <summary>What one step of the chain found.</summary>
public enum ResolutionStepResult
{
    Found = 1,
    Empty = 2,

    /// <summary>It had people, but all were excluded: the requester, the person getting access, or the policy maker (AR-2.7).</summary>
    AllExcluded = 3,

    /// <summary>The application, role or workgroup isn't there, or has nothing active.</summary>
    NotFound = 4,
    Unavailable = 5,
}

/// <summary>
/// One step of the chain, as evidence (DL-069e): the source, what it found, and the active version and content hash read.
/// <see cref="Reference"/> is the workgroup key, or the application id and role type id. Counts only, never names.
/// </summary>
public sealed record ResolutionStep(
    ApproverSourceKind Source,
    ResolutionStepResult Result,
    string? Reference,
    int? Version,
    string? Hash,
    int Found,
    int Excluded)
{
    public const int MaxReferenceLength = 80;
}

/// <summary>
/// The answer for one level: who may act, and the steps that led there. <see cref="People"/> is set only when
/// <see cref="Outcome"/> is <see cref="ResolutionOutcome.Resolved"/>.
/// </summary>
public sealed record ApproverResolution(ResolutionOutcome Outcome, IReadOnlyList<PersonId> People, IReadOnlyList<ResolutionStep> Steps)
{
    /// <summary>A role, then the owner, then the fallback workgroup: three steps at most today.</summary>
    public const int MaxSteps = 8;

    /// <summary>The outcome only: logging this object must not expose a person.</summary>
    public override string ToString() => $"{nameof(ApproverResolution)} {Outcome}";
}
