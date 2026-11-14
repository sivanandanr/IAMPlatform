using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Catalog.Domain.Applications;

/// <summary>
/// A request to activate one exact draft of an application's settings (spec 2.2 "submitting needs a second person", DL-066b):
/// the first activation of an application, or a later change of its owner or roles. Decided with the shared rules
/// (<see cref="SecondPersonDecision"/>); in addition the proposed owner can't decide it. It names the draft's version number,
/// content hash and owner, so a draft edited afterwards can't be activated by it.
/// </summary>
public sealed class ApplicationSettingsRequest
{
    private readonly SecondPersonDecision _decision;

    private ApplicationSettingsRequest(Guid id, Guid applicationId, Guid versionId, int versionNumber, string contentHash, string proposedOwner, SecondPersonDecision decision)
    {
        Id = id;
        ApplicationId = applicationId;
        SettingsVersionId = versionId;
        VersionNumber = versionNumber;
        ContentHash = contentHash;
        ProposedOwner = proposedOwner;
        _decision = decision;
    }

    public Guid Id { get; }

    public Guid ApplicationId { get; }

    public Guid SettingsVersionId { get; }

    public int VersionNumber { get; }

    public string ContentHash { get; }

    /// <summary>The owner the draft names; that person can't decide the request (DL-066b).</summary>
    public string ProposedOwner { get; }

    public string Maker => _decision.Maker;

    public string Reason => _decision.Reason;

    public DateTimeOffset ProposedAtUtc => _decision.ProposedAtUtc;

    public SecondPersonState State => _decision.State;

    public string? Checker => _decision.Checker;

    public string? DecisionReason => _decision.DecisionReason;

    public DateTimeOffset? DecidedAtUtc => _decision.DecidedAtUtc;

    /// <summary>Proposes activating the application's current draft exactly as it is. A required role must not be empty (AR-2.3).</summary>
    public static ApplicationSettingsRequest Propose(Guid id, Application application, string maker, string? reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (id == Guid.Empty)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
        }

        var draft = application.CurrentDraft ?? throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
        if (draft.HasEmptyRequiredRole)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.RequiredRoleEmpty);
        }

        var decision = CatalogSecondPerson.Shared(() => SecondPersonDecision.Propose(maker, reason, now));
        return new ApplicationSettingsRequest(id, application.Id, draft.Id, draft.VersionNumber, draft.ContentHash, draft.Owner, decision);
    }

    /// <summary>Rebuilds a stored request; a record these rules forbid is refused as damaged.</summary>
    public static ApplicationSettingsRequest Restore(
        Guid id,
        Guid applicationId,
        Guid versionId,
        int versionNumber,
        string contentHash,
        string proposedOwner,
        string maker,
        string reason,
        DateTimeOffset proposedAtUtc,
        SecondPersonState state,
        string? checker,
        string? decisionReason,
        DateTimeOffset? decidedAtUtc)
    {
        var valid = id != Guid.Empty && applicationId != Guid.Empty && versionId != Guid.Empty && versionNumber >= 1
            && CatalogRules.IsHash(contentHash) && SecondPersonText.IsPerson(proposedOwner)
            && !(state is SecondPersonState.Approved or SecondPersonState.Rejected && string.Equals(checker, proposedOwner, StringComparison.Ordinal));
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

        return new ApplicationSettingsRequest(id, applicationId, versionId, versionNumber, contentHash, proposedOwner, decision);
    }

    /// <summary>
    /// Refuses unless <paramref name="checker"/> could decide now: still proposed, not the maker, not the proposed owner, and
    /// not named as a person holding a role in the settings the request names (DL-067a; group holders are checked when groups
    /// are resolved, stage 4).
    /// </summary>
    public void RequireDecider(string checker, Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        CatalogSecondPerson.Shared(() => _decision.RequireDecider(checker));
        if (string.Equals(checker, ProposedOwner, StringComparison.Ordinal))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.OwnerCannotDecide);
        }

        if (application.Id != ApplicationId)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
        }

        if (application.Versions.SingleOrDefault(v => v.Id == SettingsVersionId) is { } version && version.NamesPersonHolder(checker))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.HolderCannotDecide);
        }
    }

    /// <summary>
    /// Approves and activates the named draft (superseding the active version). Every check runs before anything changes,
    /// so a refusal leaves the request and the application as they were. The caller stores both together.
    /// </summary>
    public void Approve(string checker, string? reason, Application application, DateTimeOffset now)
    {
        RequireDecider(checker, application);
        CatalogSecondPerson.Shared(() => SecondPersonDecision.RequireOptionalReason(reason));
        application.RequireActivatable(VersionNumber, ContentHash);
        CatalogSecondPerson.Shared(() => _decision.Approve(checker, reason, now));
        application.Activate(VersionNumber, ContentHash, Id);
    }

    public void Reject(string checker, string? reason, Application application, DateTimeOffset now)
    {
        RequireDecider(checker, application);
        CatalogSecondPerson.Shared(() => _decision.Reject(checker, reason, now));
    }

    public void Withdraw(string actor, string? reason, DateTimeOffset now) => CatalogSecondPerson.Shared(() => _decision.Withdraw(actor, reason, now));
}
