using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.Modules.Catalog.Domain.Applications;

namespace IAM.Modules.Catalog.Domain.Entitlements;

/// <summary>
/// One entitlement of an application: a group, mailbox or similar item an account can hold (spec v3.2 §2.5, §6.3, DL-084h,
/// DL-087). Its <see cref="Id"/>, <see cref="ApplicationId"/> and <see cref="Key"/> never change; its plain name can be
/// changed by someone who manages entitlements (a name is not access, CLAUDE.md rule 11). Everything that decides access lives
/// in numbered <see cref="Versions"/>: at most one draft and at most one active. A draft becomes active only through a request
/// a second person approves, after a new owner has accepted it. The entitlement can be requested once it has an active
/// version (slice 3 adds the other checks).
/// </summary>
public sealed class Entitlement
{
    private readonly List<EntitlementVersion> _versions = [];

    private Entitlement()
    {
        Key = string.Empty;
        DisplayName = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid ApplicationId { get; private set; }

    public string Key { get; private set; }

    public string DisplayName { get; private set; }

    public IReadOnlyList<EntitlementVersion> Versions => _versions;

    public EntitlementVersion? CurrentDraft => _versions.SingleOrDefault(v => v.State == SettingsVersionState.Draft);

    public EntitlementVersion? CurrentActive => _versions.SingleOrDefault(v => v.State == SettingsVersionState.Active);

    /// <summary>Registers a new entitlement with draft version 1. It is not active until that draft is approved.</summary>
    public static Entitlement Register(Guid applicationId, string key, string displayName, EntitlementSettings settings, string editor)
    {
        if (applicationId == Guid.Empty || !SecondPersonText.IsPerson(editor))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
        }

        CatalogRules.ValidateKey(key, "Key");
        CatalogRules.ValidateDisplayName(displayName, CatalogRules.DisplayNameMaxLength, "DisplayName");
        EntitlementRules.Validate(settings);
        var entitlement = new Entitlement { Id = Guid.CreateVersion7(), ApplicationId = applicationId, Key = key, DisplayName = displayName };
        entitlement._versions.Add(EntitlementVersion.CreateDraft(entitlement, 1, settings, editor));
        return entitlement;
    }

    /// <summary>A name is not access (rule 11): no second person. Returns false if unchanged.</summary>
    public bool Rename(string displayName)
    {
        CatalogRules.ValidateDisplayName(displayName, CatalogRules.DisplayNameMaxLength, "DisplayName");
        if (string.Equals(DisplayName, displayName, StringComparison.Ordinal))
        {
            return false;
        }

        DisplayName = displayName;
        return true;
    }

    /// <summary>Replaces the draft content. An owner's earlier acceptance no longer applies once the content changes. Returns false if unchanged.</summary>
    public bool UpdateDraft(EntitlementSettings settings, string editor)
    {
        EntitlementRules.Validate(settings);
        var draft = CurrentDraft ?? throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
        return draft.Apply(Key, settings, editor);
    }

    /// <summary>Starts the next version as a draft; refused while a draft exists (see <see cref="Application.StartNewDraft"/>).</summary>
    public EntitlementVersion StartNewDraft(EntitlementSettings settings, int highestVersionNumber, string editor)
    {
        EntitlementRules.Validate(settings);
        if (CurrentDraft is not null)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.DraftExists);
        }

        if (CurrentActive is null || highestVersionNumber < _versions.Max(v => v.VersionNumber))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
        }

        var next = EntitlementVersion.CreateDraft(this, highestVersionNumber + 1, settings, editor);
        _versions.Add(next);
        return next;
    }

    /// <summary>
    /// Whether the draft names an owner the active version doesn't, so the owner must accept before it can be proposed
    /// (spec v3.2 §6.3 "The new owner must accept", DL-087b). The first version always needs acceptance.
    /// </summary>
    public bool DraftNeedsAcceptance =>
        CurrentDraft is { } draft && !(CurrentActive is { } active && active.OwnerKind == draft.OwnerKind && string.Equals(active.OwnerId, draft.OwnerId, StringComparison.Ordinal));

    /// <summary>
    /// The named owner accepts the draft exactly as it is (its content hash). For a workgroup owner, the caller has checked
    /// that <paramref name="person"/> is an active member and says so with <paramref name="isOwner"/>; for a person owner,
    /// the person must be the owner. Returns false if this person already accepted this content.
    /// </summary>
    public bool AcceptOwnership(string person, bool isOwner, string contentHash, DateTimeOffset now)
    {
        var draft = CurrentDraft ?? throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
        if (!DraftNeedsAcceptance)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.NothingToAccept);
        }

        var named = draft.OwnerKind == RoleHolderKind.Person ? string.Equals(draft.OwnerId, person, StringComparison.Ordinal) : isOwner;
        if (!named)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.NotOwner);
        }

        if (!string.Equals(draft.ContentHash, contentHash, StringComparison.Ordinal))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.DraftChanged);
        }

        return draft.Accept(person, now);
    }

    /// <summary>Activates exactly the draft the request named and supersedes the active version (only an approved request calls this).</summary>
    internal void Activate(int versionNumber, string contentHash, Guid requestId)
    {
        var draft = RequireActivatable(versionNumber, contentHash);
        CurrentActive?.MarkSuperseded();
        draft.MarkActive(requestId);
    }

    /// <summary>Checks, without changing anything, that the named draft is unchanged and accepted by its owner where needed.</summary>
    internal EntitlementVersion RequireActivatable(int versionNumber, string contentHash)
    {
        var draft = CurrentDraft ?? throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
        if (draft.VersionNumber != versionNumber || !string.Equals(draft.ContentHash, contentHash, StringComparison.Ordinal))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.DraftChanged);
        }

        return DraftNeedsAcceptance && !draft.IsAccepted ? throw new ApplicationRefusedException(ApplicationRefusal.OwnerNotAccepted) : draft;
    }
}

/// <summary>
/// One numbered version of an entitlement. A draft can change; active and superseded versions never do (database triggers
/// refuse it). The owner's acceptance is part of the draft and is cleared whenever the content changes.
/// </summary>
public sealed class EntitlementVersion
{
    private EntitlementVersion()
    {
        TechnicalName = string.Empty;
        Description = string.Empty;
        OwnerId = string.Empty;
        EscalationWorkgroup = string.Empty;
        AllowedAccountClasses = string.Empty;
        ContentHash = string.Empty;
        EditedBy = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid EntitlementId { get; private set; }

    public int VersionNumber { get; private set; }

    public SettingsVersionState State { get; private set; }

    public EntitlementType Type { get; private set; }

    public string TechnicalName { get; private set; }

    public string Description { get; private set; }

    public RoleHolderKind OwnerKind { get; private set; }

    public string OwnerId { get; private set; }

    public string EscalationWorkgroup { get; private set; }

    public bool Privileged { get; private set; }

    public EntitlementRisk Risk { get; private set; }

    /// <summary>The stored form (<see cref="EntitlementRules.ToStored"/>).</summary>
    public string AllowedAccountClasses { get; private set; }

    public AccessForMode AccessForMode { get; private set; }

    public int? AccessForDays { get; private set; }

    public int? AccessForDefaultDays { get; private set; }

    public int? AccessForMaxDays { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the canonical content (<see cref="EntitlementRules.ComputeContentHash"/>).</summary>
    public string ContentHash { get; private set; }

    /// <summary>
    /// Who wrote this content: the person who created the draft or last changed it. Accepting ownership doesn't change it, so
    /// it names the author even after the owner has accepted; the author can't be the second person (DL-087).
    /// </summary>
    public string EditedBy { get; private set; }

    /// <summary>Who accepted ownership of this exact content (the owner, or a member of the owning workgroup); none until then.</summary>
    public string? OwnerAcceptedBy { get; private set; }

    public DateTimeOffset? OwnerAcceptedAtUtc { get; private set; }

    /// <summary>The approved request that activated this version; none while it is a draft.</summary>
    public Guid? ActivatedByRequestId { get; private set; }

    public bool IsAccepted => OwnerAcceptedBy is not null;

    internal static EntitlementVersion CreateDraft(Entitlement entitlement, int versionNumber, EntitlementSettings settings, string editor)
    {
        var version = new EntitlementVersion
        {
            Id = Guid.CreateVersion7(),
            EntitlementId = entitlement.Id,
            VersionNumber = versionNumber,
            State = SettingsVersionState.Draft,
        };
        version.Apply(entitlement.Key, settings, editor);
        return version;
    }

    /// <summary>The content as settings. A stored value these rules forbid is refused as a damaged record.</summary>
    public EntitlementSettings ToSettings()
    {
        var settings = new EntitlementSettings(
            Type,
            TechnicalName,
            Description,
            OwnerKind,
            OwnerId,
            EscalationWorkgroup,
            Privileged,
            Risk,
            EntitlementRules.FromStored(AllowedAccountClasses),
            new AccessFor(AccessForMode, AccessForDays, AccessForDefaultDays, AccessForMaxDays));
        try
        {
            EntitlementRules.Validate(settings);
        }
        catch (Exception ex) when (ex is ApplicationRejectedException or ApplicationRefusedException)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord, ex);
        }

        return settings;
    }

    internal bool Apply(string key, EntitlementSettings settings, string editor)
    {
        if (State != SettingsVersionState.Draft)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.NoDraft);
        }

        if (!SecondPersonText.IsPerson(editor))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.NotAllowed);
        }

        var hash = EntitlementRules.ComputeContentHash(key, VersionNumber, settings);
        if (hash == ContentHash)
        {
            return false;
        }

        Type = settings.Type;
        TechnicalName = settings.TechnicalName;
        Description = settings.Description;
        OwnerKind = settings.OwnerKind;
        OwnerId = settings.OwnerId;
        EscalationWorkgroup = settings.EscalationWorkgroup;
        Privileged = settings.Privileged;
        Risk = settings.Risk;
        AllowedAccountClasses = EntitlementRules.ToStored(settings.AllowedAccountClasses);
        AccessForMode = settings.AccessFor.Mode;
        AccessForDays = settings.AccessFor.Days;
        AccessForDefaultDays = settings.AccessFor.DefaultDays;
        AccessForMaxDays = settings.AccessFor.MaxDays;
        ContentHash = hash;
        EditedBy = editor;

        // What was accepted was the old content; the owner accepts again.
        OwnerAcceptedBy = null;
        OwnerAcceptedAtUtc = null;
        return true;
    }

    internal bool Accept(string person, DateTimeOffset now)
    {
        if (string.Equals(OwnerAcceptedBy, person, StringComparison.Ordinal))
        {
            return false;
        }

        OwnerAcceptedBy = person;
        OwnerAcceptedAtUtc = now;
        return true;
    }

    internal void MarkActive(Guid requestId)
    {
        State = SettingsVersionState.Active;
        ActivatedByRequestId = requestId;
    }

    internal void MarkSuperseded()
    {
        if (State != SettingsVersionState.Active)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
        }

        State = SettingsVersionState.Superseded;
    }
}
