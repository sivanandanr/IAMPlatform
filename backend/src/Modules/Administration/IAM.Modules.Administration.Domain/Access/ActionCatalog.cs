namespace IAM.Modules.Administration.Domain.Access;

/// <summary>
/// One action the platform can authorize (spec 12.3, ADR-0008). Ids are stable and defined in code; which roles hold them
/// is data (DL-059c). <see cref="Everyone"/> actions belong to anyone signed in and are never given by a role.
/// </summary>
public sealed record ActionDefinition(string Id, string Description, bool Everyone);

/// <summary>The one master list of actions (spec 12.3). Endpoints name one of these; nothing else gives access.</summary>
public static class ActionCatalog
{
    public const string SelfRead = "identity.self.read";
    public const string OwnPermissionsRead = "administration.permissions.read-own";
    public const string RolesRead = "administration.roles.read";
    public const string ChangesRead = "administration.changes.read";
    public const string ChangesPropose = "administration.changes.propose";
    public const string ChangesApprove = "administration.changes.approve";
    public const string ApprovalsEscalate = "workflow.approvals.escalate";

    public static IReadOnlyList<ActionDefinition> All { get; } = Array.AsReadOnly(
    [
        new ActionDefinition(SelfRead, "See who you are signed in as", Everyone: true),
        new ActionDefinition(OwnPermissionsRead, "See your own permissions", Everyone: true),
        new ActionDefinition(RolesRead, "See roles and what they allow", Everyone: false),
        new ActionDefinition(ChangesRead, "See proposed and decided access changes", Everyone: false),
        new ActionDefinition(ChangesPropose, "Propose an access change", Everyone: false),
        new ActionDefinition(ChangesApprove, "Approve or reject someone else's access change", Everyone: false),
        new ActionDefinition(ApprovalsEscalate, "Choose new approvers for a held approval stage (DL-057a; not wired yet)", Everyone: false),
    ]);

    private static readonly Dictionary<string, ActionDefinition> ById = All.ToDictionary(a => a.Id, StringComparer.Ordinal);

    public static bool IsKnown(string? id) => id is not null && ById.ContainsKey(id);

    public static bool IsEveryone(string id) => ById.TryGetValue(id, out var action) && action.Everyone;

    public static IReadOnlyList<string> EveryoneActions { get; } = Array.AsReadOnly(All.Where(a => a.Everyone).Select(a => a.Id).ToArray());

    /// <summary>
    /// Duties one person must never hold together (DL-057d, DL-060a, DL-061b): proposing a change and approving it, and
    /// proposing a change and escalating its approval. Code, not data, so no change request can relax it.
    /// </summary>
    public static IReadOnlyList<(string First, string Second)> ConflictingActions { get; } =
    [
        (ChangesPropose, ChangesApprove),
        (ChangesPropose, ApprovalsEscalate),
    ];
}

/// <summary>Why an access action was refused. Stable codes for the later API response. A refusal changes nothing.</summary>
public enum AccessRefusal
{
    InvalidSubject = 1,
    InvalidRoleCode = 2,
    InvalidRoleName = 3,
    UnknownRole = 4,
    RoleNotAssignable = 5,
    UnknownAction = 6,
    ActionNotGrantable = 7,
    DuplicateAction = 8,
    TooManyActions = 9,
    AlreadyAssigned = 10,
    NotAssigned = 11,
    IncompatibleRoles = 12,
    PairConflictsWithAssignments = 13,
    PairAlreadyExists = 14,
    UnknownPair = 15,
    SameRoleTwice = 16,
    WouldLockOut = 17,
    NotAllowed = 18,
    SelfGrant = 19,
    MakerCannotDecide = 20,
    NotProposed = 21,
    NotMaker = 22,
    InvalidReason = 23,
    AlreadyBootstrapped = 24,
    SameBootstrapPerson = 25,
    InvalidChange = 26,
    ReservedRole = 27,
    TooManyRoles = 28,
    TooManyAssignments = 29,

    /// <summary>A role, or one person's roles together, would hold two conflicting duties (<see cref="ActionCatalog.ConflictingActions"/>).</summary>
    SeparatedDuties = 30,

    /// <summary>The person who proposed can no longer propose, so their proposal can't be approved.</summary>
    MakerNoLongerAllowed = 31,
}

/// <summary>An access action was refused. The message gives the code only, never a person, role name or reason text.</summary>
public sealed class AccessRefusedException : Exception
{
    public AccessRefusedException()
    {
    }

    public AccessRefusedException(string message)
        : base(message)
    {
    }

    public AccessRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AccessRefusedException(AccessRefusal refusal)
        : base($"Access action refused: {refusal}.")
    {
        Refusal = refusal;
    }

    public AccessRefusedException(AccessRefusal refusal, Exception innerException)
        : base($"Access action refused: {refusal}.", innerException)
    {
        Refusal = refusal;
    }

    public AccessRefusal? Refusal { get; }
}

/// <summary>Provisional limits (DL-008-style sign-off needed): they only bound the work one model or change can cause.</summary>
public static class AccessLimits
{
    /// <summary>The same bound as every stored actor (ADR-0017), so anyone who can be assigned a role can also act and be audited.</summary>
    public const int MaxSubjectLength = 200;
    public const int MaxRoleCodeLength = 64;
    public const int MaxRoleNameLength = 200;
    public const int MaxReasonLength = 1_000;
    public const int MaxRoles = 200;
    public const int MaxAssignments = 10_000;
}
