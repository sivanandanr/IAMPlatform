namespace IAM.Modules.Workflow.Domain.Approvals;

/// <summary>How many approvals close a stage (IAM-WF-004).</summary>
public enum QuorumMode
{
    /// <summary>Every candidate when the stage opened or was escalated, except the requester, beneficiary and policy maker.</summary>
    All = 1,

    /// <summary>The first eligible decision closes the stage (IAM-WF-011).</summary>
    AnyOne = 2,

    /// <summary>At least N distinct eligible people.</summary>
    MinimumN = 3,
}

/// <summary>
/// What a stage does at its deadline (DL-013). There is deliberately no "approve" and no "next level": no configuration
/// can turn a timeout into a grant or skip a stage (K.1, CLAUDE.md rule 3).
/// </summary>
public enum TimeoutAction
{
    /// <summary>The stage holds until a new set of approvers is given. A new decision is still needed: escalation never closes a stage.</summary>
    Escalate = 1,
    Reject = 2,
    Cancel = 3,
}

/// <summary>Why a route, binding or action was refused. Stable codes for the later API response. A refusal changes nothing.</summary>
public enum ApprovalRefusal
{
    EmptyRoute = 1,
    TooManyStages = 2,
    InvalidStageId = 3,
    DuplicateStageId = 4,
    InvalidSequence = 5,
    InvalidQuorum = 6,
    InvalidCandidates = 7,
    InvalidDeadline = 8,
    InvalidTimeoutAction = 9,
    MandatoryStageNotApplicable = 10,
    NoApplicableStage = 11,
    MandatoryStageRemoved = 12,
    MandatoryStageReplaced = 13,
    UnknownStage = 14,
    InvalidBinding = 15,
    InstanceClosed = 16,
    InstanceHeld = 17,
    StaleVersion = 18,
    TimeWentBackwards = 19,
    StageNotActive = 20,
    StagePastDeadline = 21,
    NotEligible = 22,
    AlreadyDecided = 23,
    DuplicateDecision = 24,
    InvalidDecision = 25,
    InvalidReason = 26,
    NotRequester = 27,
    StageNotEscalatable = 28,
    InvalidActor = 29,
    EscalatorConflict = 30,
    TooManyEscalations = 31,
    TimeOutOfRange = 32,
    EscalationWouldShrinkAll = 33,
    EscalationWouldCloseStage = 34,
    EscalationRemovesAvailableApprover = 35,
    EscalationCannotReachQuorum = 36,
    EscalationRelistsEarlierDecider = 37,
}

/// <summary>An action was refused. The message gives the code only, never a person, reason text or other value.</summary>
public sealed class ApprovalRefusedException : Exception
{
    public ApprovalRefusedException()
    {
    }

    public ApprovalRefusedException(string message)
        : base(message)
    {
    }

    public ApprovalRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ApprovalRefusedException(ApprovalRefusal refusal)
        : base($"Approval action refused: {refusal}.")
    {
        Refusal = refusal;
    }

    public ApprovalRefusal? Refusal { get; }
}

/// <summary>
/// Limits on a route. <b>Provisional:</b> the spec requires no fixed stage limit (IAM-WF-001) and sets no numbers; these
/// only bound the work a route can cause and need the same sign-off as DL-008.
/// </summary>
public static class ApprovalLimits
{
    public const int MaxStages = 64;
    public const int MaxCandidatesPerStage = 256;
    public const int MaxReasonLength = 1_000;
    public const int MaxEscalationsPerStage = 8;

    /// <summary>
    /// Times outside this range are refused. It stops one bad timestamp freezing an instance, and with
    /// <see cref="MaxDeadline"/> it means deadline arithmetic cannot overflow. <c>now</c> must still come from the server clock.
    /// </summary>
    public static readonly DateTimeOffset EarliestTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc cref="EarliestTime"/>
    public static readonly DateTimeOffset LatestTime = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Bounds date arithmetic. SLA numbers are not decided (DL-010) and come from configuration.</summary>
    public static readonly TimeSpan MaxDeadline = TimeSpan.FromDays(3_650);
}

/// <summary>
/// One stage as configured, with its approvers already resolved (approver resolution is a later story). Stages with the
/// same <see cref="Sequence"/> run in parallel. <see cref="Applicable"/> is the stage condition's result when the route
/// was built; a mandatory stage must be applicable.
/// </summary>
public sealed record StageDefinition(
    Guid StageId,
    int Sequence,
    QuorumMode Quorum,
    int? MinimumApprovals,
    bool Mandatory,
    bool Independent,
    bool Applicable,
    TimeSpan Deadline,
    TimeoutAction OnTimeout,
    IReadOnlyList<Guid> Candidates);

/// <summary>Changes an entitlement may make to a route (IAM-WF-010). Mandatory stages can be neither removed nor replaced.</summary>
public sealed record RouteOverride(
    IReadOnlyList<Guid> RemovedStageIds,
    IReadOnlyList<StageDefinition> ReplacedStages,
    IReadOnlyList<StageDefinition> AddedStages);

/// <summary>A validated route: the stages an approval instance will run. Immutable; only <see cref="Create"/> and <see cref="WithOverride"/> build one.</summary>
public sealed class ApprovalRoute
{
    private ApprovalRoute(IReadOnlyList<StageDefinition> stages)
    {
        Stages = stages;
    }

    /// <summary>Ordered by sequence, then stage id.</summary>
    public IReadOnlyList<StageDefinition> Stages { get; }

    public static ApprovalRoute Create(IReadOnlyList<StageDefinition>? stages)
    {
        if (stages is null || stages.Count == 0)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.EmptyRoute);
        }

        if (stages.Count > ApprovalLimits.MaxStages)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.TooManyStages);
        }

        var ids = new HashSet<Guid>();
        var copies = new List<StageDefinition>(stages.Count);
        foreach (var stage in stages)
        {
            if (stage is null || stage.StageId == Guid.Empty)
            {
                throw new ApprovalRefusedException(ApprovalRefusal.InvalidStageId);
            }

            if (!ids.Add(stage.StageId))
            {
                throw new ApprovalRefusedException(ApprovalRefusal.DuplicateStageId);
            }

            Validate(stage);

            // A private copy: a caller's list cannot change the route after this point.
            copies.Add(stage with { Candidates = Array.AsReadOnly(stage.Candidates.ToArray()) });
        }

        // No applicable stage would mean a request with nothing to approve it. That must never read as approved.
        if (!copies.Any(s => s.Applicable))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.NoApplicableStage);
        }

        return new ApprovalRoute(Array.AsReadOnly([.. copies.OrderBy(s => s.Sequence).ThenBy(s => s.StageId)]));
    }

    /// <summary>
    /// Applies an entitlement override: it may add stages, or remove or replace non-mandatory ones, but cannot suppress a
    /// mandatory control (IAM-WF-010, K.1). A replacement keeps its place: it cannot make a stage mandatory-free by
    /// replacing a mandatory one, and it cannot move a stage to run before or after a different step. Removing a stage and
    /// adding the same id is refused, so that cannot move one either.
    /// </summary>
    public ApprovalRoute WithOverride(RouteOverride change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var stages = Stages.ToDictionary(s => s.StageId);
        foreach (var id in change.RemovedStageIds ?? [])
        {
            if (!stages.TryGetValue(id, out var existing))
            {
                throw new ApprovalRefusedException(ApprovalRefusal.UnknownStage);
            }

            if (existing.Mandatory)
            {
                throw new ApprovalRefusedException(ApprovalRefusal.MandatoryStageRemoved);
            }

            stages.Remove(id);
        }

        foreach (var replacement in change.ReplacedStages ?? [])
        {
            if (replacement is null || !stages.TryGetValue(replacement.StageId, out var existing))
            {
                throw new ApprovalRefusedException(ApprovalRefusal.UnknownStage);
            }

            if (existing.Mandatory)
            {
                throw new ApprovalRefusedException(ApprovalRefusal.MandatoryStageReplaced);
            }

            stages[replacement.StageId] = replacement with { Sequence = existing.Sequence };
        }

        var added = change.AddedStages ?? [];
        var removed = change.RemovedStageIds ?? [];
        if (added.Any(a => a is not null && removed.Contains(a.StageId)))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.DuplicateStageId);
        }

        return Create([.. stages.Values, .. added]);
    }

    private static void Validate(StageDefinition stage)
    {
        if (stage.Sequence < 1)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidSequence);
        }

        if (stage.Mandatory && !stage.Applicable)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.MandatoryStageNotApplicable);
        }

        if (!Enum.IsDefined(stage.OnTimeout))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidTimeoutAction);
        }

        if (stage.Deadline <= TimeSpan.Zero || stage.Deadline > ApprovalLimits.MaxDeadline)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidDeadline);
        }

        if (!CandidatesAreValid(stage.Candidates))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidCandidates);
        }

        var quorumValid = stage.Quorum switch
        {
            QuorumMode.All or QuorumMode.AnyOne => stage.MinimumApprovals is null,
            QuorumMode.MinimumN => stage.MinimumApprovals >= 1 && stage.MinimumApprovals <= stage.Candidates.Count,
            _ => false,
        };
        if (!quorumValid)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidQuorum);
        }
    }

    /// <summary>At least one, within the limit, no empty id, nobody listed twice (one person must not count as two).</summary>
    internal static bool CandidatesAreValid(IReadOnlyList<Guid>? candidates) =>
        candidates is { Count: > 0 and <= ApprovalLimits.MaxCandidatesPerStage }
        && !candidates.Contains(Guid.Empty)
        && candidates.Distinct().Count() == candidates.Count;
}
