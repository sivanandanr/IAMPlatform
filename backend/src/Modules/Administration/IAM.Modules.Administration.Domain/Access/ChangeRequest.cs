using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Administration.Domain.Access;

public enum ChangeState
{
    Proposed = 1,
    Approved = 2,
    Rejected = 3,
    Withdrawn = 4,
}

/// <summary>
/// One proposed access change and its decision: the shared second-person approval (spec 12.2, DL-016, DL-059b). The maker
/// can never decide it, nobody can grant themselves a role or widen a role they hold, and every rule is checked again on
/// approval against the model as it then is. Approved, rejected and withdrawn are final. A refusal changes nothing.
/// No expiry yet (DL-059m).
/// </summary>
public sealed class ChangeRequest
{
    private readonly SecondPersonDecision _decision;

    private ChangeRequest(Guid id, AccessChange change, SecondPersonDecision decision, long proposedAgainstVersion)
    {
        Id = id;
        Change = change;
        _decision = decision;
        ProposedAgainstVersion = proposedAgainstVersion;
    }

    public Guid Id { get; }

    public AccessChange Change { get; }

    public string Maker => _decision.Maker;

    public string Reason => _decision.Reason;

    public DateTimeOffset ProposedAtUtc => _decision.ProposedAtUtc;

    /// <summary>The model version the maker saw. Approval checks again against the current one; this is evidence, not a lock.</summary>
    public long ProposedAgainstVersion { get; }

    public ChangeState State => (ChangeState)_decision.State;

    public string? Checker => _decision.Checker;

    public string? DecisionReason => _decision.DecisionReason;

    public DateTimeOffset? DecidedAtUtc => _decision.DecidedAtUtc;

    /// <summary>The model version this change produced, once approved.</summary>
    public long? AppliedAsVersion { get; private set; }

    public static ChangeRequest Propose(Guid id, AccessChange change, string maker, string? reason, AccessModel model, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(model);
        if (id == Guid.Empty)
        {
            throw new AccessRefusedException(AccessRefusal.InvalidChange);
        }

        if (!AccessText.IsSubject(maker) || !model.Can(maker, ActionCatalog.ChangesPropose))
        {
            throw new AccessRefusedException(AccessRefusal.NotAllowed);
        }

        RequireNotSelfGrant(change, maker, model);
        if (!AccessText.IsReason(reason))
        {
            throw new AccessRefusedException(AccessRefusal.InvalidReason);
        }

        model.Check(change);
        return new ChangeRequest(id, change, Shared(() => SecondPersonDecision.Propose(maker, reason, now)), model.Version);
    }

    /// <summary>
    /// Rebuilds a stored change request. Its fields are checked again, so a damaged store can't produce one the rules
    /// forbid: the shared second-person shape (a decided change needs a decider other than the maker, or the maker for a
    /// withdrawal, and a time), and only an approved change has an applied version, newer than the one it was proposed on.
    /// </summary>
    public static ChangeRequest Restore(
        Guid id,
        AccessChange change,
        string maker,
        string reason,
        DateTimeOffset proposedAtUtc,
        long proposedAgainstVersion,
        ChangeState state,
        string? checker,
        string? decisionReason,
        DateTimeOffset? decidedAtUtc,
        long? appliedAsVersion)
    {
        ArgumentNullException.ThrowIfNull(change);
        SecondPersonDecision decision;
        try
        {
            decision = SecondPersonDecision.Restore(maker, reason, proposedAtUtc, (SecondPersonState)state, checker, decisionReason, decidedAtUtc);
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new AccessRefusedException(AccessRefusal.InvalidChange, ex);
        }

        var valid = id != Guid.Empty
            && proposedAgainstVersion >= 0
            && AccessText.IsSubject(maker)
            && (checker is null || AccessText.IsSubject(checker))
            && AccessText.IsReason(reason)
            && (decisionReason is null || AccessText.IsReason(decisionReason))
            && (state == ChangeState.Approved ? appliedAsVersion > proposedAgainstVersion : appliedAsVersion is null);
        return !valid
            ? throw new AccessRefusedException(AccessRefusal.InvalidChange)
            : new ChangeRequest(id, change, decision, proposedAgainstVersion) { AppliedAsVersion = appliedAsVersion };
    }

    /// <summary>Approves the change and returns the model it produces. The caller stores both together, or neither.</summary>
    public AccessModel Approve(string checker, string? reason, AccessModel current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        RequireDecider(checker, current);
        if (reason is not null && !AccessText.IsReason(reason))
        {
            throw new AccessRefusedException(AccessRefusal.InvalidReason);
        }

        // Checked again now: the model may have changed since the proposal, for example someone gained a role, or the maker
        // lost the right to propose (then the proposal no longer has a proposer).
        if (!current.Can(Maker, ActionCatalog.ChangesPropose))
        {
            throw new AccessRefusedException(AccessRefusal.MakerNoLongerAllowed);
        }

        RequireNotSelfGrant(Change, Maker, current);
        RequireNotSelfGrant(Change, checker, current);
        var next = current.Apply(Change);
        Shared(() => _decision.Approve(checker, reason, now));
        AppliedAsVersion = next.Version;
        return next;
    }

    public void Reject(string checker, string? reason, AccessModel current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        RequireDecider(checker, current);
        if (!AccessText.IsReason(reason))
        {
            throw new AccessRefusedException(AccessRefusal.InvalidReason);
        }

        Shared(() => _decision.Reject(checker, reason, now));
    }

    /// <summary>Only the maker can withdraw their own proposal.</summary>
    public void Withdraw(string actor, string? reason, DateTimeOffset now) => Shared(() => _decision.Withdraw(actor, reason, now));

    /// <summary>
    /// Nobody may give themselves a role, or change the actions of a role they hold: that would widen their own access
    /// without anyone else deciding it.
    /// </summary>
    private static void RequireNotSelfGrant(AccessChange change, string person, AccessModel model)
    {
        var selfGrant = change switch
        {
            AssignRole assign => string.Equals(assign.Subject, person, StringComparison.Ordinal),
            DefineRole define => model.RolesOf(person).Contains(define.Code, StringComparer.Ordinal),
            _ => false,
        };
        if (selfGrant)
        {
            throw new AccessRefusedException(AccessRefusal.SelfGrant);
        }
    }

    /// <summary>Runs a shared rule and reports its refusal in access-control terms.</summary>
    private static T Shared<T>(Func<T> rule)
    {
        try
        {
            return rule();
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new AccessRefusedException(Map(ex.Refusal), ex);
        }
    }

    private static void Shared(Action rule) => Shared(() =>
    {
        rule();
        return true;
    });

    private static AccessRefusal Map(SecondPersonRefusal? refusal) => refusal switch
    {
        SecondPersonRefusal.NotProposed => AccessRefusal.NotProposed,
        SecondPersonRefusal.MakerCannotDecide => AccessRefusal.MakerCannotDecide,
        SecondPersonRefusal.NotMaker => AccessRefusal.NotMaker,
        SecondPersonRefusal.InvalidReason => AccessRefusal.InvalidReason,
        SecondPersonRefusal.InvalidPerson => AccessRefusal.NotAllowed,
        _ => AccessRefusal.InvalidChange,
    };

    private void RequireDecider(string checker, AccessModel current)
    {
        Shared(() => _decision.RequireDecider(checker));
        if (!AccessText.IsSubject(checker) || !current.Can(checker, ActionCatalog.ChangesApprove))
        {
            throw new AccessRefusedException(AccessRefusal.NotAllowed);
        }
    }
}
