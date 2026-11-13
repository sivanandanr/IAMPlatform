using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Identity.Domain.Attributes;

/// <summary>
/// A request to publish one exact draft of an attribute (DL-007, DL-064, IAM-WF-019), decided by a second person with the
/// shared rules (<see cref="SecondPersonDecision"/>). It names the draft's version number and content hash when proposed,
/// so editing the draft afterwards makes it impossible to approve. Approving publishes that draft and retires the
/// previously published version, in one step. Who may propose or decide is checked by the caller against permissions.
/// </summary>
public sealed class AttributePublishRequest
{
    private readonly SecondPersonDecision _decision;

    private AttributePublishRequest(Guid id, Guid definitionId, Guid versionId, int versionNumber, string contentHash, SecondPersonDecision decision)
    {
        Id = id;
        AttributeDefinitionId = definitionId;
        AttributeDefinitionVersionId = versionId;
        VersionNumber = versionNumber;
        ContentHash = contentHash;
        _decision = decision;
    }

    public Guid Id { get; }

    public Guid AttributeDefinitionId { get; }

    public Guid AttributeDefinitionVersionId { get; }

    public int VersionNumber { get; }

    /// <summary>The draft's content hash when proposed: approval publishes only a draft that still has it.</summary>
    public string ContentHash { get; }

    public string Maker => _decision.Maker;

    public string Reason => _decision.Reason;

    public DateTimeOffset ProposedAtUtc => _decision.ProposedAtUtc;

    public SecondPersonState State => _decision.State;

    public string? Checker => _decision.Checker;

    public string? DecisionReason => _decision.DecisionReason;

    public DateTimeOffset? DecidedAtUtc => _decision.DecidedAtUtc;

    /// <summary>Proposes publishing the definition's current draft, exactly as it is now.</summary>
    public static AttributePublishRequest Propose(Guid id, AttributeDefinition definition, string maker, string? reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (id == Guid.Empty)
        {
            throw new AttributePublishRefusedException(AttributePublishRefusal.InvalidRecord);
        }

        var draft = definition.CurrentDraft ?? throw new AttributePublishRefusedException(AttributePublishRefusal.NoDraft);
        var decision = Shared(() => SecondPersonDecision.Propose(maker, reason, now));
        return new AttributePublishRequest(id, definition.Id, draft.Id, draft.VersionNumber, draft.ContentHash, decision);
    }

    /// <summary>Rebuilds a stored request; a record the shared rules or these fields forbid is refused as damaged.</summary>
    public static AttributePublishRequest Restore(
        Guid id,
        Guid definitionId,
        Guid versionId,
        int versionNumber,
        string contentHash,
        string maker,
        string reason,
        DateTimeOffset proposedAtUtc,
        SecondPersonState state,
        string? checker,
        string? decisionReason,
        DateTimeOffset? decidedAtUtc)
    {
        var valid = id != Guid.Empty && definitionId != Guid.Empty && versionId != Guid.Empty && versionNumber >= 1
            && contentHash is { Length: AttributeRules.HashLength } && contentHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        if (!valid)
        {
            throw new AttributePublishRefusedException(AttributePublishRefusal.InvalidRecord);
        }

        SecondPersonDecision decision;
        try
        {
            decision = SecondPersonDecision.Restore(maker, reason, proposedAtUtc, state, checker, decisionReason, decidedAtUtc);
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new AttributePublishRefusedException(AttributePublishRefusal.InvalidRecord, ex);
        }

        return new AttributePublishRequest(id, definitionId, versionId, versionNumber, contentHash, decision);
    }

    /// <summary>Approves and publishes the named draft (retiring the previous version). The caller stores both together.</summary>
    public void Approve(string checker, string? reason, AttributeDefinition definition, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Shared(() => _decision.RequireDecider(checker));
        Shared(() => SecondPersonDecision.RequireOptionalReason(reason));
        if (definition.Id != AttributeDefinitionId)
        {
            throw new AttributePublishRefusedException(AttributePublishRefusal.InvalidRecord);
        }

        definition.Publish(VersionNumber, ContentHash, Id);
        Shared(() => _decision.Approve(checker, reason, now));
    }

    /// <summary>Refuses unless <paramref name="checker"/> could decide this request now (still proposed, not the maker).</summary>
    public void RequireDecider(string checker) => Shared(() => _decision.RequireDecider(checker));

    public void Reject(string checker, string? reason, DateTimeOffset now) => Shared(() => _decision.Reject(checker, reason, now));

    public void Withdraw(string actor, string? reason, DateTimeOffset now) => Shared(() => _decision.Withdraw(actor, reason, now));

    private static T Shared<T>(Func<T> rule)
    {
        try
        {
            return rule();
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new AttributePublishRefusedException(Map(ex.Refusal), ex);
        }
    }

    private static void Shared(Action rule) => Shared(() =>
    {
        rule();
        return true;
    });

    private static AttributePublishRefusal Map(SecondPersonRefusal? refusal) => refusal switch
    {
        SecondPersonRefusal.NotProposed => AttributePublishRefusal.NotProposed,
        SecondPersonRefusal.MakerCannotDecide => AttributePublishRefusal.MakerCannotDecide,
        SecondPersonRefusal.NotMaker => AttributePublishRefusal.NotMaker,
        SecondPersonRefusal.InvalidReason => AttributePublishRefusal.InvalidReason,
        SecondPersonRefusal.InvalidPerson => AttributePublishRefusal.NotAllowed,
        _ => AttributePublishRefusal.InvalidRecord,
    };
}
