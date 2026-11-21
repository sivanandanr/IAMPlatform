namespace IAM.Modules.Workflow.Contracts;

/// <summary>How an approval's stored log compares with the audit store (DL-093, R-037).</summary>
public enum ApprovalIntegrityStatus
{
    /// <summary>The log replays and every action has exactly one matching, intact audit record.</summary>
    Verified = 1,

    /// <summary>No such approval.</summary>
    NotFound = 2,

    /// <summary>The stored log, history or summary doesn't replay.</summary>
    Damaged = 3,

    /// <summary>The log replays, but the audit store doesn't agree with it.</summary>
    Mismatch = 4,

    /// <summary>The approval kept changing while it was checked; check again later.</summary>
    Changing = 5,
}

/// <summary>One stage of the route an approval was bound to, as replayed (DL-096). Enum-like fields carry their stable names.</summary>
public sealed record ApprovedStage(
    Guid StageId,
    int Sequence,
    string Kind,
    string Source,
    string? WorkgroupKey,
    Guid? ApplicationId,
    Guid? RoleTypeId,
    Guid? EntitlementId,
    string Quorum,
    int? MinimumApprovals,
    bool Mandatory,
    bool Independent,
    int DeadlineHours,
    string OnTimeout);

/// <summary>The matrix evidence and route an approval was bound to, as replayed (DL-096).</summary>
public sealed record ApprovedRoute(Guid MatrixVersionId, string MatrixContentHash, Guid MatchedRuleId, string PolicyMaker, IReadOnlyList<ApprovedStage> Stages);

/// <summary>
/// The outcome of checking one approval (DL-093). <see cref="Problem"/> is a code, never data. The people, the
/// authorisation and <see cref="ActionsOpen"/> (an Action level that isn't finished yet, so provisioning must wait) are the
/// replayed ones, and only meaningful when <see cref="Status"/> is <see cref="ApprovalIntegrityStatus.Verified"/>.
/// <see cref="CompletedActions"/> says when each completed Action level was completed (DL-097): what was given on it after
/// that was given too late.
/// </summary>
public sealed record ApprovalIntegrityReport(
    Guid InstanceId,
    ApprovalIntegrityStatus Status,
    string? Problem,
    Guid RequestItemId,
    string? Requester,
    string? Beneficiary,
    bool ProvisioningAuthorized,
    bool ActionsOpen,
    int Actions,
    ApprovedRoute? Route = null,
    IReadOnlyDictionary<Guid, DateTimeOffset>? CompletedActions = null)
{
    public static ApprovalIntegrityReport Failed(Guid instanceId, ApprovalIntegrityStatus status, string problem) =>
        new(instanceId, status, problem, Guid.Empty, null, null, ProvisioningAuthorized: false, ActionsOpen: false, Actions: 0);

    /// <summary>The id and status only: logging this object must not expose a person.</summary>
    public override string ToString() => $"{nameof(ApprovalIntegrityReport)} {InstanceId} {Status} {Problem}";
}

/// <summary>
/// Checks an approval's stored action log against the audit store before anything acts on its authorisation (R-037 release
/// gate, DL-075, DL-093). Read-only: it changes and records nothing, and the caller decides what a failure means.
/// </summary>
public interface IApprovalIntegrity
{
    Task<ApprovalIntegrityReport> CheckAsync(Guid instanceId, CancellationToken cancellationToken);
}
