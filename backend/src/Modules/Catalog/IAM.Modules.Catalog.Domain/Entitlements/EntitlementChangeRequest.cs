using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.Modules.Catalog.Domain.Applications;

namespace IAM.Modules.Catalog.Domain.Entitlements;

/// <summary>
/// A request to activate one exact draft of an entitlement (DL-084h, DL-087): its first activation or a later change. It
/// names the draft's version number, content hash, owner and the person who accepted ownership, so a draft edited afterwards
/// can't be activated by it. Decided with the shared second-person rules; in addition the owner, the person who accepted
/// ownership and (checked by the caller) members of an owning workgroup can't decide it.
/// </summary>
public sealed class EntitlementChangeRequest
{
    private readonly SecondPersonDecision _decision;

    private EntitlementChangeRequest(
        Guid id, Guid entitlementId, Guid versionId, int versionNumber, string contentHash, RoleHolderKind ownerKind, string ownerId, string? acceptedBy, SecondPersonDecision decision)
    {
        Id = id;
        EntitlementId = entitlementId;
        VersionId = versionId;
        VersionNumber = versionNumber;
        ContentHash = contentHash;
        OwnerKind = ownerKind;
        OwnerId = ownerId;
        AcceptedBy = acceptedBy;
        _decision = decision;
    }

    public Guid Id { get; }

    public Guid EntitlementId { get; }

    public Guid VersionId { get; }

    public int VersionNumber { get; }

    public string ContentHash { get; }

    public RoleHolderKind OwnerKind { get; }

    public string OwnerId { get; }

    /// <summary>Who accepted ownership of the named content; none when the owner is unchanged from the active version.</summary>
    public string? AcceptedBy { get; }

    public string Maker => _decision.Maker;

    public string Reason => _decision.Reason;

    public DateTimeOffset ProposedAtUtc => _decision.ProposedAtUtc;

    public SecondPersonState State => _decision.State;

    public string? Checker => _decision.Checker;

    public string? DecisionReason => _decision.DecisionReason;

    public DateTimeOffset? DecidedAtUtc => _decision.DecidedAtUtc;

    /// <summary>Proposes activating the current draft exactly as it is. A new owner must have accepted it first.</summary>
    public static EntitlementChangeRequest Propose(Guid id, Entitlement entitlement, string maker, string? reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        if (id == Guid.Empty)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
        }

        var draft = entitlement.CurrentDraft ?? throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
        var needsAcceptance = entitlement.DraftNeedsAcceptance;
        if (needsAcceptance && !draft.IsAccepted)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.OwnerNotAccepted);
        }

        var decision = CatalogSecondPerson.Shared(() => SecondPersonDecision.Propose(maker, reason, now));
        return new EntitlementChangeRequest(
            id, entitlement.Id, draft.Id, draft.VersionNumber, draft.ContentHash, draft.OwnerKind, draft.OwnerId, needsAcceptance ? draft.OwnerAcceptedBy : null, decision);
    }

    /// <summary>Rebuilds a stored request; a record these rules forbid is refused as damaged.</summary>
    public static EntitlementChangeRequest Restore(
        Guid id,
        Guid entitlementId,
        Guid versionId,
        int versionNumber,
        string contentHash,
        RoleHolderKind ownerKind,
        string ownerId,
        string? acceptedBy,
        string maker,
        string reason,
        DateTimeOffset proposedAtUtc,
        SecondPersonState state,
        string? checker,
        string? decisionReason,
        DateTimeOffset? decidedAtUtc)
    {
        var decided = state is SecondPersonState.Approved or SecondPersonState.Rejected;
        var valid = id != Guid.Empty && entitlementId != Guid.Empty && versionId != Guid.Empty && versionNumber >= 1
            && CatalogRules.IsHash(contentHash)
            && (ownerKind == RoleHolderKind.Person ? SecondPersonText.IsPerson(ownerId) : ownerKind == RoleHolderKind.Group && EntitlementRules.IsKey(ownerId))
            && (acceptedBy is null || SecondPersonText.IsPerson(acceptedBy))
            && !(decided && (string.Equals(checker, acceptedBy, StringComparison.Ordinal)
                || (ownerKind == RoleHolderKind.Person && string.Equals(checker, ownerId, StringComparison.Ordinal))));
        if (!valid)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
        }

        SecondPersonDecision decision;
        try
        {
            decision = SecondPersonDecision.Restore(maker, reason, proposedAtUtc, state, checker, decisionReason, decidedAtUtc);
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord, ex);
        }

        return new EntitlementChangeRequest(id, entitlementId, versionId, versionNumber, contentHash, ownerKind, ownerId, acceptedBy, decision);
    }

    /// <summary>
    /// Refuses unless <paramref name="checker"/> could decide now: still proposed, not the maker, not the owner, not the person
    /// who accepted ownership, and not an active member of an owning workgroup (<paramref name="memberOfOwner"/>, which the
    /// caller works out from the Organization contract).
    /// </summary>
    public void RequireDecider(string checker, Entitlement entitlement, bool memberOfOwner)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        CatalogSecondPerson.Shared(() => _decision.RequireDecider(checker));
        if (entitlement.Id != EntitlementId)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
        }

        if ((OwnerKind == RoleHolderKind.Person && string.Equals(checker, OwnerId, StringComparison.Ordinal)) || memberOfOwner)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.OwnerCannotDecide);
        }

        if (string.Equals(checker, AcceptedBy, StringComparison.Ordinal))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.AcceptorCannotDecide);
        }
    }

    /// <summary>Approves and activates the named draft. Every check runs before anything changes.</summary>
    public void Approve(string checker, string? reason, Entitlement entitlement, bool memberOfOwner, DateTimeOffset now)
    {
        RequireDecider(checker, entitlement, memberOfOwner);
        CatalogSecondPerson.Shared(() => SecondPersonDecision.RequireOptionalReason(reason));
        var draft = entitlement.RequireActivatable(VersionNumber, ContentHash);
        if (string.Equals(draft.EditedBy, checker, StringComparison.Ordinal))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.MakerCannotDecide);
        }

        // The acceptance the request names must still be the draft's (an edit clears it, and a fresh one is another person's).
        if (AcceptedBy is not null && !string.Equals(draft.OwnerAcceptedBy, AcceptedBy, StringComparison.Ordinal))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.DraftChanged);
        }

        CatalogSecondPerson.Shared(() => _decision.Approve(checker, reason, now));
        entitlement.Activate(VersionNumber, ContentHash, Id);
    }

    public void Reject(string checker, string? reason, Entitlement entitlement, bool memberOfOwner, DateTimeOffset now)
    {
        RequireDecider(checker, entitlement, memberOfOwner);
        CatalogSecondPerson.Shared(() => _decision.Reject(checker, reason, now));
    }

    public void Withdraw(string actor, string? reason, DateTimeOffset now) => CatalogSecondPerson.Shared(() => _decision.Withdraw(actor, reason, now));
}
