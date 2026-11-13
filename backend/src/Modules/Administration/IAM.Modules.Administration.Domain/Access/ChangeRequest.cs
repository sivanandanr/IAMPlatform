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
    private ChangeRequest(Guid id, AccessChange change, string maker, string reason, DateTimeOffset proposedAtUtc, long proposedAgainstVersion)
    {
        Id = id;
        Change = change;
        Maker = maker;
        Reason = reason;
        ProposedAtUtc = proposedAtUtc;
        ProposedAgainstVersion = proposedAgainstVersion;
        State = ChangeState.Proposed;
    }

    public Guid Id { get; }

    public AccessChange Change { get; }

    public string Maker { get; }

    public string Reason { get; }

    public DateTimeOffset ProposedAtUtc { get; }

    /// <summary>The model version the maker saw. Approval checks again against the current one; this is evidence, not a lock.</summary>
    public long ProposedAgainstVersion { get; }

    public ChangeState State { get; private set; }

    public string? Checker { get; private set; }

    public string? DecisionReason { get; private set; }

    public DateTimeOffset? DecidedAtUtc { get; private set; }

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
        return new ChangeRequest(id, change, maker, reason!, now.ToUniversalTime(), model.Version);
    }

    /// <summary>
    /// Rebuilds a stored change request. Its fields are checked again, so a damaged store can't produce one the rules
    /// forbid: a decided change needs a decider other than the maker (or the maker, for a withdrawal) and a time, and only
    /// an approved change has an applied version.
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
        var valid = id != Guid.Empty
            && AccessText.IsSubject(maker)
            && AccessText.IsReason(reason)
            && proposedAgainstVersion >= 0
            && Enum.IsDefined(state)
            && (decisionReason is null || AccessText.IsReason(decisionReason))
            && state switch
            {
                ChangeState.Proposed => checker is null && decisionReason is null && decidedAtUtc is null && appliedAsVersion is null,
                ChangeState.Withdrawn => string.Equals(checker, maker, StringComparison.Ordinal) && decidedAtUtc is not null && appliedAsVersion is null,
                ChangeState.Rejected => AccessText.IsSubject(checker) && !string.Equals(checker, maker, StringComparison.Ordinal) && decisionReason is not null && decidedAtUtc is not null && appliedAsVersion is null,
                ChangeState.Approved => AccessText.IsSubject(checker) && !string.Equals(checker, maker, StringComparison.Ordinal) && decidedAtUtc is not null && appliedAsVersion > proposedAgainstVersion,
                _ => false,
            };
        if (!valid)
        {
            throw new AccessRefusedException(AccessRefusal.InvalidChange);
        }

        return new ChangeRequest(id, change, maker, reason, proposedAtUtc, proposedAgainstVersion)
        {
            State = state,
            Checker = checker,
            DecisionReason = decisionReason,
            DecidedAtUtc = decidedAtUtc,
            AppliedAsVersion = appliedAsVersion,
        };
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
        Decide(ChangeState.Approved, checker, reason, now);
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

        Decide(ChangeState.Rejected, checker, reason, now);
    }

    /// <summary>Only the maker can withdraw their own proposal.</summary>
    public void Withdraw(string actor, string? reason, DateTimeOffset now)
    {
        RequireProposed();
        if (!string.Equals(actor, Maker, StringComparison.Ordinal))
        {
            throw new AccessRefusedException(AccessRefusal.NotMaker);
        }

        if (reason is not null && !AccessText.IsReason(reason))
        {
            throw new AccessRefusedException(AccessRefusal.InvalidReason);
        }

        Decide(ChangeState.Withdrawn, actor, reason, now);
    }

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

    private void RequireDecider(string checker, AccessModel current)
    {
        RequireProposed();
        if (string.Equals(checker, Maker, StringComparison.Ordinal))
        {
            throw new AccessRefusedException(AccessRefusal.MakerCannotDecide);
        }

        if (!AccessText.IsSubject(checker) || !current.Can(checker, ActionCatalog.ChangesApprove))
        {
            throw new AccessRefusedException(AccessRefusal.NotAllowed);
        }
    }

    private void RequireProposed()
    {
        if (State != ChangeState.Proposed)
        {
            throw new AccessRefusedException(AccessRefusal.NotProposed);
        }
    }

    private void Decide(ChangeState state, string by, string? reason, DateTimeOffset now)
    {
        State = state;
        Checker = by;
        DecisionReason = reason;
        DecidedAtUtc = now.ToUniversalTime();
    }
}
