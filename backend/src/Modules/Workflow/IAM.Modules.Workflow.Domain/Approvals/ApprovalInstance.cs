using System.Globalization;

namespace IAM.Modules.Workflow.Domain.Approvals;

/// <summary>Where an instance stands. Approved, Rejected, Cancelled and Withdrawn never change.</summary>
public enum ApprovalState
{
    InProgress = 1,

    /// <summary>A stage has too few eligible approvers, or timed out. Accepts no decisions until a valid route is given.</summary>
    Held = 2,
    Approved = 3,
    Rejected = 4,
    Cancelled = 5,
    Withdrawn = 6,
}

public enum StageState
{
    Pending = 1,
    Active = 2,
    Held = 3,
    Approved = 4,
    Rejected = 5,
    Cancelled = 6,

    /// <summary>The stage's condition was false when the route was built. Never a mandatory stage.</summary>
    NotApplicable = 7,
}

public enum ApprovalEventKind
{
    Bound = 1,
    StageActivated = 2,
    StageHeld = 3,
    StageNotApplicable = 4,
    DecisionApproved = 5,
    DecisionRejected = 6,
    StageApproved = 7,
    StageRejected = 8,
    StageCancelled = 9,
    StageTimedOut = 10,
    StageEscalated = 11,
    InstanceHeld = 12,
    InstanceResumed = 13,
    InstanceApproved = 14,
    InstanceRejected = 15,
    InstanceCancelled = 16,
    InstanceWithdrawn = 17,
}

/// <summary>What the instance is bound to (IAM-WF-009, FR-POL-103). Taken once; a later policy change does not alter it.</summary>
public sealed record ApprovalBinding(
    Guid InstanceId,
    Guid RequestItemId,
    Guid PolicyVersionId,
    string PolicyHash,
    Guid MatchedRuleId,
    Guid SnapshotReference,
    Guid Requester,
    Guid Beneficiary,
    Guid PolicyMaker)
{
    /// <summary>The instance id only: logging this object must not expose a person.</summary>
    public override string ToString() => $"{nameof(ApprovalBinding)} {InstanceId}";
}

/// <summary>
/// One decision as submitted. <see cref="Actor"/> and <see cref="OnBehalfOf"/> must already be authenticated and any
/// delegation verified by the calling layer; this class checks eligibility, not identity or that a delegation exists.
/// </summary>
public sealed record ApprovalDecision(
    Guid DecisionId,
    Guid StageId,
    Guid Actor,
    Guid? OnBehalfOf,
    bool Approve,
    string? Reason,
    long ExpectedVersion)
{
    /// <summary>The id only: logging this object must not expose a person or a reason.</summary>
    public override string ToString() => $"{nameof(ApprovalDecision)} {DecisionId}";
}

/// <summary>A recorded decision. <see cref="Principal"/> is whose approval it counts as: the delegator, or the actor.</summary>
public sealed record RecordedDecision(Guid DecisionId, Guid StageId, Guid Actor, Guid Principal, bool Approve, string? Reason, DateTimeOffset AtUtc)
{
    /// <summary>The id only: logging this object must not expose a person or a reason.</summary>
    public override string ToString() => $"{nameof(RecordedDecision)} {DecisionId}";
}

/// <summary>Who changed a stage's approvers, from whom to whom, why, and the stage's deadline afterwards (IAM-WF-006, IAM-WF-009).</summary>
public sealed record EscalationRecord(
    Guid StageId,
    Guid Actor,
    string Reason,
    IReadOnlyList<Guid> PreviousCandidates,
    IReadOnlyList<Guid> NewCandidates,
    IReadOnlyList<Guid> PreviousRequired,
    IReadOnlyList<Guid> NewRequired,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset AtUtc)
{
    /// <summary>The stage id only: logging this object must not expose a person or a reason.</summary>
    public override string ToString() => $"{nameof(EscalationRecord)} {StageId}";
}

/// <summary>Who cancelled or withdrew the request, and why.</summary>
public sealed record ApprovalClosure(ApprovalState Outcome, Guid Actor, string? Reason, DateTimeOffset AtUtc)
{
    /// <summary>The outcome only: logging this object must not expose a person or a reason.</summary>
    public override string ToString() => $"{nameof(ApprovalClosure)} {Outcome}";
}

/// <summary>One history entry, in order. Holds ids and codes only: no attribute values or reason text (IAM-WF-020).</summary>
public sealed record ApprovalEvent(long Version, DateTimeOffset AtUtc, ApprovalEventKind Kind, Guid? StageId, Guid? Actor)
{
    /// <summary>No actor: logging this object must not expose a person.</summary>
    public override string ToString() => $"{nameof(ApprovalEvent)} {Version} {Kind} {StageId}";
}

/// <summary>A read-only view of a stage. <see cref="ConfiguredCandidates"/> is the route's list; <see cref="Candidates"/> is the current one.</summary>
public sealed record StageView(
    Guid StageId,
    int Sequence,
    StageState State,
    bool Mandatory,
    bool Independent,
    QuorumMode Quorum,
    int? MinimumApprovals,
    DateTimeOffset? DeadlineUtc,
    IReadOnlyList<Guid> ConfiguredCandidates,
    IReadOnlyList<Guid> Candidates,
    IReadOnlyList<Guid> Required,
    IReadOnlyList<Guid> Eligible,
    IReadOnlyList<Guid> Approvers,
    int Escalations);

/// <summary>
/// Approval state machine (ADR-0005, IAM-WF-004, K.1), proof of concept (P0-009): in-memory, no storage, and it emits
/// nothing. Actions are checked first and change nothing when refused. Time is always given, never read from a clock, so
/// the same sequence of actions gives the same history.
/// <para>
/// Only <see cref="ApprovalState.Approved"/> means every applicable stage met its quorum with eligible, distinct people,
/// and only then is <see cref="ProvisioningAuthorized"/> true. Only a decision can close a stage as approved: a timeout, a
/// missing approver, an empty candidate list or an escalation can hold, reject or cancel, but never approve.
/// </para>
/// </summary>
public sealed class ApprovalInstance
{
    private readonly List<StageRun> _stages;
    private readonly List<RecordedDecision> _decisions = [];
    private readonly List<ApprovalEvent> _history = [];
    private readonly List<EscalationRecord> _escalations = [];
    private readonly HashSet<Guid> _excluded;
    private readonly HashSet<Guid> _escalators = [];
    private readonly HashSet<Guid> _decisionIds = [];
    private int _step = -1;
    private DateTimeOffset _lastAt;

    private ApprovalInstance(ApprovalBinding binding, ApprovalRoute route, DateTimeOffset atUtc)
    {
        Binding = binding;
        _lastAt = atUtc;

        // Never eligible, on any stage (K.1, K.6).
        _excluded = [binding.Requester, binding.Beneficiary, binding.PolicyMaker];
        _stages = [.. route.Stages.Select(s => new StageRun(s))];
        State = ApprovalState.InProgress;
    }

    public ApprovalBinding Binding { get; }

    public ApprovalState State { get; private set; }

    /// <summary>The number of history events so far; several may come from one action. A decision must name the version it was made against.</summary>
    public long Version { get; private set; }

    public IReadOnlyList<RecordedDecision> Decisions => _decisions.AsReadOnly();

    public IReadOnlyList<ApprovalEvent> History => _history.AsReadOnly();

    public IReadOnlyList<EscalationRecord> Escalations => _escalations.AsReadOnly();

    /// <summary>Set when the request is cancelled by a person or withdrawn; null otherwise.</summary>
    public ApprovalClosure? Closure { get; private set; }

    public IReadOnlyList<StageView> Stages => [.. _stages.Select(View)];

    /// <summary>
    /// True only when approved and every stage was either not applicable or approved with its quorum still met by the
    /// recorded decisions. Counted again here rather than trusted.
    /// </summary>
    public bool ProvisioningAuthorized =>
        State == ApprovalState.Approved
        && _stages.All(s => (s.State == StageState.Approved && QuorumMet(s)) || (s.State == StageState.NotApplicable && !s.Definition.Mandatory))
        && _stages.Any(s => s.State == StageState.Approved);

    private bool IsClosed => State is ApprovalState.Approved or ApprovalState.Rejected or ApprovalState.Cancelled or ApprovalState.Withdrawn;

    /// <summary>Starts an instance on a validated route. If the first stage has no eligible approver the instance starts Held, never approved.</summary>
    public static ApprovalInstance Bind(ApprovalBinding binding, ApprovalRoute route, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(route);
        if (!IsValid(binding))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidBinding);
        }

        RequireTimeInRange(now);
        var at = now.ToUniversalTime();
        var instance = new ApprovalInstance(binding, route, at);
        instance.Record(at, ApprovalEventKind.Bound, null, null);
        foreach (var stage in instance._stages.Where(s => !s.Definition.Applicable))
        {
            stage.State = StageState.NotApplicable;
            instance.Record(at, ApprovalEventKind.StageNotApplicable, stage.Definition.StageId, null);
        }

        instance.Settle(at);
        return instance;
    }

    /// <summary>Records one decision. Any eligible Reject on a stage rejects the whole request (DL-042).</summary>
    public void Decide(ApprovalDecision decision, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var at = RequireOpen(decision.ExpectedVersion, now);
        if (decision.DecisionId == Guid.Empty || decision.Actor == Guid.Empty || decision.OnBehalfOf == Guid.Empty || decision.OnBehalfOf == decision.Actor)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidDecision);
        }

        if (_decisionIds.Contains(decision.DecisionId))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.DuplicateDecision);
        }

        if (State == ApprovalState.Held)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InstanceHeld);
        }

        var stage = Stage(decision.StageId);
        if (stage.State != StageState.Active)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.StageNotActive);
        }

        // Too late is too late, even if the timeout has not been processed yet: expiry never grants (IAM-WF-006).
        if (at >= stage.DeadlineUtc)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.StagePastDeadline);
        }

        var principal = decision.OnBehalfOf ?? decision.Actor;
        if (!stage.ActiveCandidates.Contains(principal) || !IsEligible(stage, principal) || !IsEligible(stage, decision.Actor))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.NotEligible);
        }

        // One person, one say on a stage: a delegate acting for two people must not count twice.
        if (HasDecided(stage, principal) || HasDecided(stage, decision.Actor))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.AlreadyDecided);
        }

        RequireValidReason(decision.Reason, required: !decision.Approve);

        _decisionIds.Add(decision.DecisionId);
        _decisions.Add(new RecordedDecision(decision.DecisionId, decision.StageId, decision.Actor, principal, decision.Approve, decision.Reason, at));
        Record(at, decision.Approve ? ApprovalEventKind.DecisionApproved : ApprovalEventKind.DecisionRejected, decision.StageId, decision.Actor);

        if (!decision.Approve)
        {
            stage.State = StageState.Rejected;
            Record(at, ApprovalEventKind.StageRejected, decision.StageId, decision.Actor);
            Close(ApprovalState.Rejected, ApprovalEventKind.InstanceRejected, at, decision.Actor);
            return;
        }

        Settle(at);
    }

    /// <summary>
    /// Applies deadlines. If stages expire together, Reject wins, then Cancel, so the result does not depend on stage ids.
    /// Escalate holds the stage until new approvers are given. Nothing here can approve a stage. If nothing has expired,
    /// nothing changes.
    /// </summary>
    public void ApplyTimeouts(DateTimeOffset now, long expectedVersion)
    {
        var at = RequireOpen(expectedVersion, now);
        var expired = _stages.Where(s => s.State is StageState.Active or StageState.Held && at >= s.DeadlineUtc).ToList();

        if (expired.Exists(s => s.Definition.OnTimeout == TimeoutAction.Reject))
        {
            CloseExpired(expired, TimeoutAction.Reject, StageState.Rejected, ApprovalEventKind.StageRejected, at);
            Close(ApprovalState.Rejected, ApprovalEventKind.InstanceRejected, at, null);
            return;
        }

        if (expired.Exists(s => s.Definition.OnTimeout == TimeoutAction.Cancel))
        {
            CloseExpired(expired, TimeoutAction.Cancel, StageState.Cancelled, ApprovalEventKind.StageCancelled, at);
            Close(ApprovalState.Cancelled, ApprovalEventKind.InstanceCancelled, at, null);
            return;
        }

        foreach (var stage in expired.Where(s => s.State == StageState.Active))
        {
            Record(at, ApprovalEventKind.StageTimedOut, stage.Definition.StageId, null);
            Hold(stage, at);
        }

        UpdateHeldState(at);
    }

    /// <summary>
    /// Gives a held stage new approvers (IAM-WF-006, DL-043, DL-047), and reopens it: an escalation that could not is
    /// refused. Only a later decision can close the stage. Nobody on a list of an open or pending stage may escalate, and
    /// nor may anyone who decided on this request; an escalator cannot decide on it later. Before the deadline, only people
    /// who can no longer act may be replaced. A Reject or Cancel stage keeps its original deadline. An ALL list never gets
    /// smaller than when the stage opened (DL-044), and nobody dropped after deciding can be listed again.
    /// </summary>
    public void Escalate(Guid stageId, IReadOnlyList<Guid> newCandidates, Guid actor, string? reason, long expectedVersion, DateTimeOffset now)
    {
        var at = RequireOpen(expectedVersion, now);
        var stage = Stage(stageId);
        if (stage.State != StageState.Held)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.StageNotEscalatable);
        }

        // The people the request is about, and whoever made the policy, cannot choose its approvers.
        if (actor == Guid.Empty || _excluded.Contains(actor))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidActor);
        }

        if (!ApprovalRoute.CandidatesAreValid(newCandidates))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidCandidates);
        }

        // Choosing the approvers and being one are separate duties, in either order and on any stage of this request.
        if (newCandidates.Contains(actor)
            || _decisions.Any(d => d.Actor == actor || d.Principal == actor)
            || _stages.Any(s => s.State is StageState.Pending or StageState.Active or StageState.Held && s.ActiveCandidates.Contains(actor)))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.EscalatorConflict);
        }

        RequireValidReason(reason, required: true);

        if (stage.Escalations >= ApprovalLimits.MaxEscalationsPerStage)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.TooManyEscalations);
        }

        var approvers = ValidApprovers(stage);
        if (at >= stage.DeadlineUtc)
        {
            // A configured Reject or Cancel has already happened in all but name; escalating must not undo it.
            if (stage.Definition.OnTimeout != TimeoutAction.Escalate)
            {
                throw new ApprovalRefusedException(ApprovalRefusal.StagePastDeadline);
            }
        }
        else if (stage.ActiveCandidates.Any(c => (approvers.Contains(c) || (IsEligible(stage, c) && !HasDecided(stage, c))) && !newCandidates.Contains(c)))
        {
            // Nobody has timed out yet, so someone's conflict must not be a way to remove the people who have none.
            throw new ApprovalRefusedException(ApprovalRefusal.EscalationRemovesAvailableApprover);
        }

        // Their earlier decision would count again with no new decision, under a list they were dropped from.
        if (newCandidates.Any(c => HasDecided(stage, c) && !stage.ActiveCandidates.Contains(c)))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.EscalationRelistsEarlierDecider);
        }

        List<Guid> required = [.. newCandidates.Where(c => !_excluded.Contains(c))];
        if (stage.Definition.Quorum == QuorumMode.All)
        {
            if (required.Count < stage.MinimumRequired)
            {
                throw new ApprovalRefusedException(ApprovalRefusal.EscalationWouldShrinkAll);
            }

            // Listing only people who already approved would close the stage with no new decision.
            if (required.Count > 0 && required.All(approvers.Contains))
            {
                throw new ApprovalRefusedException(ApprovalRefusal.EscalationWouldCloseStage);
            }
        }

        if (QuorumUnreachable(stage, newCandidates, required))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.EscalationCannotReachQuorum);
        }

        var previousCandidates = stage.ActiveCandidates;
        var previousRequired = stage.Required;
        stage.ActiveCandidates = [.. newCandidates];
        stage.Required = required;
        stage.Escalations++;
        stage.State = StageState.Active;
        if (stage.Definition.OnTimeout == TimeoutAction.Escalate)
        {
            stage.DeadlineUtc = at + stage.Definition.Deadline;
        }

        _escalators.Add(actor);
        _escalations.Add(new EscalationRecord(
            stageId,
            actor,
            reason!,
            Array.AsReadOnly(previousCandidates.ToArray()),
            Array.AsReadOnly(newCandidates.ToArray()),
            Array.AsReadOnly(previousRequired.ToArray()),
            Array.AsReadOnly(required.ToArray()),
            stage.DeadlineUtc!.Value,
            at));

        Record(at, ApprovalEventKind.StageEscalated, stageId, actor);
        Record(at, ApprovalEventKind.StageActivated, stageId, null);
        Settle(at);
    }

    /// <summary>Withdraws the request. Only the requester may; the calling layer has authenticated them.</summary>
    public void Withdraw(Guid actor, string? reason, long expectedVersion, DateTimeOffset now)
    {
        var at = RequireOpen(expectedVersion, now);
        if (actor != Binding.Requester)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.NotRequester);
        }

        RequireValidReason(reason, required: false);
        Closure = new ApprovalClosure(ApprovalState.Withdrawn, actor, reason, at);
        Close(ApprovalState.Withdrawn, ApprovalEventKind.InstanceWithdrawn, at, actor);
    }

    /// <summary>Cancels the request. Who may do this is for the calling layer's authorization.</summary>
    public void Cancel(Guid actor, string? reason, long expectedVersion, DateTimeOffset now)
    {
        var at = RequireOpen(expectedVersion, now);
        if (actor == Guid.Empty)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidActor);
        }

        RequireValidReason(reason, required: true);
        Closure = new ApprovalClosure(ApprovalState.Cancelled, actor, reason, at);
        Close(ApprovalState.Cancelled, ApprovalEventKind.InstanceCancelled, at, actor);
    }

    private static bool IsValid(ApprovalBinding binding) =>
        binding.InstanceId != Guid.Empty && binding.RequestItemId != Guid.Empty && binding.PolicyVersionId != Guid.Empty
        && binding.MatchedRuleId != Guid.Empty && binding.SnapshotReference != Guid.Empty && binding.Requester != Guid.Empty
        && binding.Beneficiary != Guid.Empty && binding.PolicyMaker != Guid.Empty
        && binding.PolicyHash is { Length: 64 } && binding.PolicyHash.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static void RequireValidReason(string? reason, bool required)
    {
        if (reason is null ? required : !IsAcceptableReason(reason))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidReason);
        }
    }

    private static bool IsAcceptableReason(string reason) =>
        reason.Length <= ApprovalLimits.MaxReasonLength
        && !reason.Any(IsUnsafeInReason)
        && HasNoLoneSurrogate(reason)
        && reason.Any(IsVisible);

    /// <summary>
    /// Control characters, line separators, and the bidirectional controls that can make text read differently from what
    /// it says. Other format characters, such as the zero-width non-joiner, are part of normal writing in several languages.
    /// </summary>
    private static bool IsUnsafeInReason(char c) =>
        char.IsControl(c) || c is (char)0x061C or (char)0x200E or (char)0x200F or (char)0x2028 or (char)0x2029 or >= (char)0x202A and <= (char)0x202E or >= (char)0x2066 and <= (char)0x2069;

    private static bool IsVisible(char c) =>
        !char.IsWhiteSpace(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format;

    private static bool HasNoLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireTimeInRange(DateTimeOffset now)
    {
        if (now < ApprovalLimits.EarliestTime || now >= ApprovalLimits.LatestTime)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.TimeOutOfRange);
        }
    }

    private DateTimeOffset RequireOpen(long expectedVersion, DateTimeOffset now)
    {
        if (IsClosed)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.InstanceClosed);
        }

        if (expectedVersion != Version)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.StaleVersion);
        }

        RequireTimeInRange(now);
        var at = now.ToUniversalTime();
        if (at < _lastAt)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.TimeWentBackwards);
        }

        return at;
    }

    private StageRun Stage(Guid stageId) =>
        _stages.Find(s => s.Definition.StageId == stageId) ?? throw new ApprovalRefusedException(ApprovalRefusal.UnknownStage);

    /// <summary>
    /// Not requester, beneficiary or policy maker; has not escalated on this request; and independent of other stages.
    /// Independence works both ways: someone who decided elsewhere cannot decide on an independent stage, and someone who
    /// decided on an independent stage cannot decide elsewhere (DL-010). The rule applies in either order, though which
    /// person decides first can still change who is left to decide.
    /// </summary>
    private bool IsEligible(StageRun stage, Guid person)
    {
        if (_excluded.Contains(person) || _escalators.Contains(person))
        {
            return false;
        }

        foreach (var decision in _decisions)
        {
            if (decision.StageId != stage.Definition.StageId && (decision.Actor == person || decision.Principal == person))
            {
                if (stage.Definition.Independent || Stage(decision.StageId).Definition.Independent)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Has this person already had their say on this stage, for themselves or for someone else?</summary>
    private bool HasDecided(StageRun stage, Guid person) =>
        _decisions.Any(d => d.StageId == stage.Definition.StageId && (d.Principal == person || d.Actor == person));

    /// <summary>People whose approval on this stage counts. The eligibility filter is a backstop: the checks on each action should mean it never removes anyone.</summary>
    private List<Guid> ValidApprovers(StageRun stage) =>
        [.. _decisions.Where(d => d.StageId == stage.Definition.StageId && d.Approve && IsEligible(stage, d.Principal) && IsEligible(stage, d.Actor)).Select(d => d.Principal).Distinct()];

    private bool QuorumMet(StageRun stage)
    {
        var approvers = ValidApprovers(stage);
        return stage.Definition.Quorum switch
        {
            QuorumMode.AnyOne => approvers.Count >= 1,
            QuorumMode.MinimumN => approvers.Count >= stage.Definition.MinimumApprovals,
            QuorumMode.All => stage.Required.Count > 0 && stage.Required.All(approvers.Contains),
            _ => false,
        };
    }

    /// <summary>True when the quorum can no longer be reached by the people available. That holds; it never passes.</summary>
    private bool QuorumUnreachable(StageRun stage, IReadOnlyList<Guid> candidates, List<Guid> required)
    {
        var approvers = ValidApprovers(stage);
        var undecided = candidates.Count(c => IsEligible(stage, c) && !HasDecided(stage, c));
        return stage.Definition.Quorum switch
        {
            QuorumMode.AnyOne => approvers.Count + undecided < 1,
            QuorumMode.MinimumN => approvers.Count + undecided < stage.Definition.MinimumApprovals,
            QuorumMode.All => required.Count == 0 || required.Any(r => !approvers.Contains(r) && (!IsEligible(stage, r) || HasDecided(stage, r))),
            _ => true,
        };
    }

    /// <summary>Closes stages that met their quorum, holds those that cannot, and moves to the next step when one is complete.</summary>
    private void Settle(DateTimeOffset at)
    {
        while (true)
        {
            foreach (var stage in CurrentStep().Where(s => s.State == StageState.Active))
            {
                if (QuorumMet(stage))
                {
                    stage.State = StageState.Approved;
                    Record(at, ApprovalEventKind.StageApproved, stage.Definition.StageId, null);
                }
                else if (QuorumUnreachable(stage, stage.ActiveCandidates, stage.Required))
                {
                    Hold(stage, at);
                }
            }

            UpdateHeldState(at);
            if (_step >= 0 && !CurrentStep().All(s => s.State == StageState.Approved))
            {
                return;
            }

            if (!ActivateNextStep(at))
            {
                Close(ApprovalState.Approved, ApprovalEventKind.InstanceApproved, at, null);
                return;
            }
        }
    }

    private IEnumerable<StageRun> CurrentStep() =>
        _step < 0 ? [] : _stages.Where(s => s.Definition.Applicable && s.Definition.Sequence == _step);

    private bool ActivateNextStep(DateTimeOffset at)
    {
        var next = _stages.Where(s => s.Definition.Applicable && s.Definition.Sequence > _step).Select(s => (int?)s.Definition.Sequence).Min();
        if (next is null)
        {
            return false;
        }

        _step = next.Value;
        foreach (var stage in CurrentStep())
        {
            stage.State = StageState.Active;
            stage.DeadlineUtc = at + stage.Definition.Deadline;

            // Fixed when the stage opens. Excluded people are removed (K.1), but an independence conflict stays on the
            // list and so holds the stage: ALL must not quietly need fewer people (DL-044).
            stage.Required = [.. stage.ActiveCandidates.Where(c => !_excluded.Contains(c))];
            stage.MinimumRequired = stage.Required.Count;
            Record(at, ApprovalEventKind.StageActivated, stage.Definition.StageId, null);
        }

        return true;
    }

    /// <summary>Every stage that expired records it, and every stage whose own timeout caused this ends in that state, whatever the stage ids.</summary>
    private void CloseExpired(List<StageRun> expired, TimeoutAction action, StageState state, ApprovalEventKind kind, DateTimeOffset at)
    {
        foreach (var stage in expired)
        {
            if (stage.Definition.OnTimeout == action)
            {
                Record(at, ApprovalEventKind.StageTimedOut, stage.Definition.StageId, null);
                stage.State = state;
                Record(at, kind, stage.Definition.StageId, null);
            }
            else if (stage.State == StageState.Active || stage.Definition.OnTimeout != TimeoutAction.Escalate)
            {
                // A stage already held by an earlier Escalate timeout recorded this then.
                Record(at, ApprovalEventKind.StageTimedOut, stage.Definition.StageId, null);
            }
        }
    }

    private void Hold(StageRun stage, DateTimeOffset at)
    {
        stage.State = StageState.Held;
        Record(at, ApprovalEventKind.StageHeld, stage.Definition.StageId, null);
    }

    private void UpdateHeldState(DateTimeOffset at)
    {
        var held = _stages.Any(s => s.State == StageState.Held);
        if (held && State == ApprovalState.InProgress)
        {
            State = ApprovalState.Held;
            Record(at, ApprovalEventKind.InstanceHeld, null, null);
        }
        else if (!held && State == ApprovalState.Held)
        {
            State = ApprovalState.InProgress;
            Record(at, ApprovalEventKind.InstanceResumed, null, null);
        }
    }

    private void Close(ApprovalState state, ApprovalEventKind kind, DateTimeOffset at, Guid? actor)
    {
        foreach (var stage in _stages.Where(s => s.State is StageState.Pending or StageState.Active or StageState.Held))
        {
            stage.State = StageState.Cancelled;
            Record(at, ApprovalEventKind.StageCancelled, stage.Definition.StageId, null);
        }

        State = state;
        Record(at, kind, null, actor);
    }

    private void Record(DateTimeOffset at, ApprovalEventKind kind, Guid? stageId, Guid? actor)
    {
        Version++;
        _lastAt = at;
        _history.Add(new ApprovalEvent(Version, at, kind, stageId, actor));
    }

    private StageView View(StageRun stage) => new(
        stage.Definition.StageId,
        stage.Definition.Sequence,
        stage.State,
        stage.Definition.Mandatory,
        stage.Definition.Independent,
        stage.Definition.Quorum,
        stage.Definition.MinimumApprovals,
        stage.DeadlineUtc,
        stage.Definition.Candidates,
        [.. stage.ActiveCandidates],
        [.. stage.Required],
        [.. stage.ActiveCandidates.Where(c => IsEligible(stage, c))],
        ValidApprovers(stage),
        stage.Escalations);

    private sealed class StageRun(StageDefinition definition)
    {
        public StageDefinition Definition { get; } = definition;

        public StageState State { get; set; } = StageState.Pending;

        public DateTimeOffset? DeadlineUtc { get; set; }

        public List<Guid> ActiveCandidates { get; set; } = [.. definition.Candidates];

        public List<Guid> Required { get; set; } = [];

        /// <summary>The size of the required list when the stage opened: an ALL list never goes below it.</summary>
        public int MinimumRequired { get; set; }

        public int Escalations { get; set; }
    }
}
