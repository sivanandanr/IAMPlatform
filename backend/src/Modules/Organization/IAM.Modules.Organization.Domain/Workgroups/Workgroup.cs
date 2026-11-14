using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Organization.Domain.Workgroups;

/// <summary>
/// An IAM workgroup (spec 1.8, IAM-WF-011, DL-068): an internal group used for approval and ownership, never for
/// application access. Its key never changes; its display name can be changed by someone who manages workgroups. Its
/// members live in numbered versions, at most one draft and one active; a draft becomes active only through a request
/// approved by a second person who is not the maker, not an author of the draft and not a member of it. Never deleted.
/// </summary>
public sealed class Workgroup
{
    private readonly List<WorkgroupVersion> _versions = [];

    private Workgroup()
    {
        Key = string.Empty;
        DisplayName = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Key { get; private set; }

    public string DisplayName { get; private set; }

    public IReadOnlyList<WorkgroupVersion> Versions => _versions;

    public WorkgroupVersion? CurrentDraft => _versions.SingleOrDefault(v => v.State == MemberVersionState.Draft);

    public WorkgroupVersion? CurrentActive => _versions.SingleOrDefault(v => v.State == MemberVersionState.Active);

    public static Workgroup Register(string key, string displayName, IReadOnlyList<string?> members)
    {
        WorkgroupRules.ValidateKey(key, "Key");
        WorkgroupRules.ValidateDisplayName(displayName);
        var people = WorkgroupRules.ValidateMembers(members);
        var workgroup = new Workgroup { Id = Guid.CreateVersion7(), Key = key, DisplayName = displayName };
        workgroup._versions.Add(WorkgroupVersion.CreateDraft(workgroup, 1, people));
        return workgroup;
    }

    /// <summary>A name is not access (rule 11): no second person. Returns false if unchanged.</summary>
    public bool Rename(string displayName)
    {
        WorkgroupRules.ValidateDisplayName(displayName);
        if (string.Equals(DisplayName, displayName, StringComparison.Ordinal))
        {
            return false;
        }

        DisplayName = displayName;
        return true;
    }

    public bool UpdateDraft(IReadOnlyList<string?> members)
    {
        var people = WorkgroupRules.ValidateMembers(members);
        var draft = CurrentDraft ?? throw new WorkgroupRefusedException(WorkgroupRefusal.NoDraft);
        return draft.Apply(Key, people);
    }

    /// <summary>Starts the next member list as a draft; the caller passes the highest stored version number.</summary>
    public WorkgroupVersion StartNewDraft(IReadOnlyList<string?> members, int highestVersionNumber)
    {
        var people = WorkgroupRules.ValidateMembers(members);
        if (CurrentDraft is not null)
        {
            throw new WorkgroupRefusedException(WorkgroupRefusal.DraftExists);
        }

        if (CurrentActive is null || highestVersionNumber < _versions.Max(v => v.VersionNumber))
        {
            throw new WorkgroupRefusedException(WorkgroupRefusal.InvalidRecord);
        }

        var next = WorkgroupVersion.CreateDraft(this, highestVersionNumber + 1, people);
        _versions.Add(next);
        return next;
    }

    internal WorkgroupVersion RequireActivatable(int versionNumber, string contentHash)
    {
        var draft = CurrentDraft ?? throw new WorkgroupRefusedException(WorkgroupRefusal.NoDraft);
        return draft.VersionNumber != versionNumber || !string.Equals(draft.ContentHash, contentHash, StringComparison.Ordinal)
            ? throw new WorkgroupRefusedException(WorkgroupRefusal.DraftChanged)
            : draft;
    }

    internal void Activate(int versionNumber, string contentHash, Guid requestId)
    {
        var draft = RequireActivatable(versionNumber, contentHash);
        CurrentActive?.MarkSuperseded();
        draft.MarkActive(requestId);
    }
}

/// <summary>One numbered member list. A draft can change; active and superseded lists never do.</summary>
public sealed class WorkgroupVersion
{
    public const string ContentHashLayout = "iam.organization.workgroup-members.v1";

    private readonly List<WorkgroupMember> _members = [];

    private WorkgroupVersion()
    {
        ContentHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid WorkgroupId { get; private set; }

    public int VersionNumber { get; private set; }

    public MemberVersionState State { get; private set; }

    public string ContentHash { get; private set; }

    public Guid? ActivatedByRequestId { get; private set; }

    public IReadOnlyList<WorkgroupMember> Members => _members;

    public IReadOnlyList<string> People => [.. _members.Select(m => m.Person).Order(StringComparer.Ordinal)];

    public bool HasMember(string? person) => person is not null && _members.Any(m => string.Equals(m.Person, person, StringComparison.Ordinal));

    internal static WorkgroupVersion CreateDraft(Workgroup workgroup, int versionNumber, IReadOnlyList<string> people)
    {
        var version = new WorkgroupVersion
        {
            Id = Guid.CreateVersion7(),
            WorkgroupId = workgroup.Id,
            VersionNumber = versionNumber,
            State = MemberVersionState.Draft,
        };
        version.Apply(workgroup.Key, people);
        return version;
    }

    /// <summary>Lower-case hex SHA-256 over the UTF-8 compact JSON array <c>[layout, key, versionNumber, [members sorted]]</c>.</summary>
    public static string ComputeContentHash(string key, int versionNumber, IReadOnlyList<string> people)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(people);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false }))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(ContentHashLayout);
            writer.WriteStringValue(key);
            writer.WriteNumberValue(versionNumber);
            writer.WriteStartArray();
            foreach (var person in people.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(person);
            }

            writer.WriteEndArray();
            writer.WriteEndArray();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    internal bool Apply(string key, IReadOnlyList<string> people)
    {
        if (State != MemberVersionState.Draft)
        {
            throw new WorkgroupRefusedException(WorkgroupRefusal.NoDraft);
        }

        var hash = ComputeContentHash(key, VersionNumber, people);
        if (hash == ContentHash)
        {
            return false;
        }

        ContentHash = hash;
        var wanted = people.ToHashSet(StringComparer.Ordinal);
        _members.RemoveAll(m => !wanted.Contains(m.Person));
        foreach (var person in people.Where(p => _members.All(m => !string.Equals(m.Person, p, StringComparison.Ordinal))))
        {
            _members.Add(new WorkgroupMember(Id, person));
        }

        return true;
    }

    internal void MarkActive(Guid requestId)
    {
        State = MemberVersionState.Active;
        ActivatedByRequestId = requestId;
    }

    internal void MarkSuperseded()
    {
        if (State != MemberVersionState.Active)
        {
            throw new WorkgroupRefusedException(WorkgroupRefusal.InvalidRecord);
        }

        State = MemberVersionState.Superseded;
    }
}

/// <summary>One member of a member list version. Stored as a row.</summary>
public sealed class WorkgroupMember
{
    internal WorkgroupMember(Guid versionId, string person)
    {
        VersionId = versionId;
        Person = person;
    }

    private WorkgroupMember()
    {
        Person = string.Empty;
    }

    public Guid VersionId { get; private set; }

    public string Person { get; private set; }
}

/// <summary>
/// A request to activate one exact member list draft, decided with the shared rules (<see cref="SecondPersonDecision"/>);
/// in addition, a person named as a member in that draft can't decide it (DL-068a).
/// </summary>
public sealed class WorkgroupChangeRequest
{
    private readonly SecondPersonDecision _decision;

    private WorkgroupChangeRequest(Guid id, Guid workgroupId, Guid versionId, int versionNumber, string contentHash, SecondPersonDecision decision)
    {
        Id = id;
        WorkgroupId = workgroupId;
        VersionId = versionId;
        VersionNumber = versionNumber;
        ContentHash = contentHash;
        _decision = decision;
    }

    public Guid Id { get; }

    public Guid WorkgroupId { get; }

    public Guid VersionId { get; }

    public int VersionNumber { get; }

    public string ContentHash { get; }

    public string Maker => _decision.Maker;

    public string Reason => _decision.Reason;

    public DateTimeOffset ProposedAtUtc => _decision.ProposedAtUtc;

    public SecondPersonState State => _decision.State;

    public string? Checker => _decision.Checker;

    public string? DecisionReason => _decision.DecisionReason;

    public DateTimeOffset? DecidedAtUtc => _decision.DecidedAtUtc;

    public static WorkgroupChangeRequest Propose(Guid id, Workgroup workgroup, string maker, string? reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(workgroup);
        if (id == Guid.Empty)
        {
            throw new WorkgroupRefusedException(WorkgroupRefusal.InvalidRecord);
        }

        var draft = workgroup.CurrentDraft ?? throw new WorkgroupRefusedException(WorkgroupRefusal.NoDraft);
        var decision = WorkgroupSecondPerson.Shared(() => SecondPersonDecision.Propose(maker, reason, now));
        return new WorkgroupChangeRequest(id, workgroup.Id, draft.Id, draft.VersionNumber, draft.ContentHash, decision);
    }

    public static WorkgroupChangeRequest Restore(
        Guid id, Guid workgroupId, Guid versionId, int versionNumber, string contentHash, string maker, string reason, DateTimeOffset proposedAtUtc,
        SecondPersonState state, string? checker, string? decisionReason, DateTimeOffset? decidedAtUtc)
    {
        if (id == Guid.Empty || workgroupId == Guid.Empty || versionId == Guid.Empty || versionNumber < 1 || !WorkgroupRules.IsHash(contentHash))
        {
            throw new WorkgroupRefusedException(WorkgroupRefusal.InvalidRecord);
        }

        SecondPersonDecision decision;
        try
        {
            decision = SecondPersonDecision.Restore(maker, reason, proposedAtUtc, state, checker, decisionReason, decidedAtUtc);
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new WorkgroupRefusedException(WorkgroupRefusal.InvalidRecord, ex);
        }

        return new WorkgroupChangeRequest(id, workgroupId, versionId, versionNumber, contentHash, decision);
    }

    /// <summary>Refuses unless <paramref name="checker"/> could decide now: still proposed, not the maker, not a member of the named list.</summary>
    public void RequireDecider(string checker, Workgroup workgroup)
    {
        ArgumentNullException.ThrowIfNull(workgroup);
        WorkgroupSecondPerson.Shared(() => _decision.RequireDecider(checker));
        if (workgroup.Id != WorkgroupId)
        {
            throw new WorkgroupRefusedException(WorkgroupRefusal.InvalidRecord);
        }

        if (workgroup.Versions.SingleOrDefault(v => v.Id == VersionId) is { } version && version.HasMember(checker))
        {
            throw new WorkgroupRefusedException(WorkgroupRefusal.MemberCannotDecide);
        }
    }

    /// <summary>Every check runs before anything changes; then the decision, then the activation.</summary>
    public void Approve(string checker, string? reason, Workgroup workgroup, DateTimeOffset now)
    {
        RequireDecider(checker, workgroup);
        WorkgroupSecondPerson.Shared(() => SecondPersonDecision.RequireOptionalReason(reason));
        workgroup.RequireActivatable(VersionNumber, ContentHash);
        WorkgroupSecondPerson.Shared(() => _decision.Approve(checker, reason, now));
        workgroup.Activate(VersionNumber, ContentHash, Id);
    }

    public void Reject(string checker, string? reason, Workgroup workgroup, DateTimeOffset now)
    {
        RequireDecider(checker, workgroup);
        WorkgroupSecondPerson.Shared(() => _decision.Reject(checker, reason, now));
    }

    public void Withdraw(string actor, string? reason, DateTimeOffset now) => WorkgroupSecondPerson.Shared(() => _decision.Withdraw(actor, reason, now));
}
