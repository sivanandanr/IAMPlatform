namespace IAM.Modules.Policy.Contracts;

/// <summary>
/// One fact the matrix may test: whose (Beneficiary, Requester, Application, Entitlement, Request), which attribute (by id),
/// its value as text (null when the source confirmed there is none) and when it was read. Values may be sensitive: a logged
/// record must not show them.
/// </summary>
public sealed record MatrixFact(string Subject, Guid AttributeId, string? Value, DateTimeOffset SynchronizedAtUtc)
{
    public override string ToString() => $"{Subject} {AttributeId:D}";
}

/// <summary>One stage of a selected route, with enum values by name (DL-090c).</summary>
public sealed record MatrixRouteStage(
    int Sequence,
    string Kind,
    string Source,
    string? RoleTypeCode,
    string? WorkgroupKey,
    string Quorum,
    int? MinimumApprovals,
    bool Mandatory,
    bool Independent,
    int DeadlineHours,
    string OnTimeout,
    IReadOnlyList<string> Fields);

/// <summary>
/// Which route the active matrix selects, with the evidence to reconstruct why (IAM-WF-020, IAM-WF-022). Only
/// <c>Matched</c> carries a route; every other outcome is a hold (CLAUDE.md rule 3). It grants and approves nothing.
/// </summary>
public sealed record MatrixSelection(
    string Outcome,
    Guid? VersionId,
    int? VersionNumber,
    string? ContentHash,
    string? CompiledHash,
    Guid? MatchedRuleId,
    IReadOnlyList<MatrixRouteStage> Route,
    string? EvidenceJson)
{
    public const string Matched = "Matched";

    /// <summary>No active matrix: nothing can be routed, so everything holds.</summary>
    public const string HoldNoMatrix = "HoldNoMatrix";

    public bool IsMatched => Outcome == Matched;
}

/// <summary>Selects a route for a request from the active approval matrix (DL-090). Read-only, for the Request module.</summary>
public interface IApprovalMatrix
{
    Task<MatrixSelection> SelectAsync(IReadOnlyList<MatrixFact> facts, DateTimeOffset asOfUtc, CancellationToken cancellationToken);
}

/// <summary>
/// The ids of the built-in fact attributes (DL-090f), for the module that fills them. The Policy domain defines the same
/// ids; a test keeps them equal.
/// </summary>
public static class BuiltInFactIds
{
    public static readonly Guid RequestAction = Guid.Parse("0193c000-0000-7000-8000-000000000001");
    public static readonly Guid RequestAccountType = Guid.Parse("0193c000-0000-7000-8000-000000000002");
    public static readonly Guid RequestNewAccount = Guid.Parse("0193c000-0000-7000-8000-000000000003");
    public static readonly Guid EntitlementKey = Guid.Parse("0193c000-0000-7000-8000-000000000011");
    public static readonly Guid EntitlementPrivileged = Guid.Parse("0193c000-0000-7000-8000-000000000012");
    public static readonly Guid EntitlementRisk = Guid.Parse("0193c000-0000-7000-8000-000000000013");
    public static readonly Guid EntitlementType = Guid.Parse("0193c000-0000-7000-8000-000000000014");
    public static readonly Guid ApplicationKey = Guid.Parse("0193c000-0000-7000-8000-000000000021");
}
