using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Policy.Domain.Matrix;

/// <summary>Where a matrix version stands. Only an approved request makes a draft active.</summary>
public enum MatrixVersionState
{
    Draft = 1,
    Active = 2,
    Superseded = 3,
}

/// <summary>Why a matrix change was refused. Codes only, never content.</summary>
public enum MatrixRefusal
{
    NotAllowed = 1,
    MakerCannotDecide = 2,
    NotProposed = 3,
    NotMaker = 4,
    InvalidReason = 5,
    InvalidRecord = 6,
    AlreadyPending = 7,
    NoDraft = 8,
    DraftChanged = 9,
    MakerNoLongerAllowed = 10,

    /// <summary>The pre-publication check found possible same-priority overlaps, or was truncated (DL-090e).</summary>
    OverlapsFound = 11,

    /// <summary>A catalog attribute the draft uses changed or was retired since the draft was compiled.</summary>
    AttributesChanged = 12,

    /// <summary>A condition names an attribute that is neither published nor a built-in fact.</summary>
    UnknownAttribute = 13,
}

/// <summary>A matrix change was refused by a rule (see <see cref="MatrixRefusal"/>).</summary>
public sealed class MatrixRefusedException : Exception
{
    public MatrixRefusedException()
    {
    }

    public MatrixRefusedException(string message)
        : base(message)
    {
    }

    public MatrixRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MatrixRefusedException(MatrixRefusal refusal, Exception? innerException = null)
        : base($"Approval matrix change refused: {refusal}.", innerException)
    {
        Refusal = refusal;
    }

    public MatrixRefusal? Refusal { get; }
}

/// <summary>
/// The enterprise approval matrix (DL-090b): one per deployment. Its rules live in numbered versions, at most one draft and
/// one active; a draft becomes active only through a request a second person approves (rule 4, ADR-0009).
/// </summary>
public sealed class ApprovalMatrix
{
    /// <summary>The only matrix key (DL-090b: one enterprise matrix, priority only).</summary>
    public const string EnterpriseKey = "enterprise";

    private readonly List<MatrixVersion> _versions = [];

    private ApprovalMatrix()
    {
        Key = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Key { get; private set; }

    public IReadOnlyList<MatrixVersion> Versions => _versions;

    public MatrixVersion? CurrentDraft => _versions.SingleOrDefault(v => v.State == MatrixVersionState.Draft);

    public MatrixVersion? CurrentActive => _versions.SingleOrDefault(v => v.State == MatrixVersionState.Active);

    public static ApprovalMatrix Create(MatrixDocument document, string compiledHash, string editor)
    {
        var matrix = new ApprovalMatrix { Id = Guid.CreateVersion7(), Key = EnterpriseKey };
        matrix._versions.Add(MatrixVersion.CreateDraft(matrix, 1, document, compiledHash, editor));
        return matrix;
    }

    /// <summary>Replaces the draft, or starts the next one after the active version. Returns false if nothing changed.</summary>
    public bool SetDraft(MatrixDocument document, string compiledHash, int highestVersionNumber, string editor)
    {
        if (CurrentDraft is { } draft)
        {
            return draft.Apply(document, compiledHash, editor);
        }

        if (CurrentActive is null || highestVersionNumber < _versions.Max(v => v.VersionNumber))
        {
            throw new MatrixRefusedException(MatrixRefusal.InvalidRecord);
        }

        _versions.Add(MatrixVersion.CreateDraft(this, highestVersionNumber + 1, document, compiledHash, editor));
        return true;
    }

    internal MatrixVersion RequireActivatable(int versionNumber, string contentHash)
    {
        var draft = CurrentDraft ?? throw new MatrixRefusedException(MatrixRefusal.NoDraft);
        return draft.VersionNumber == versionNumber && string.Equals(draft.ContentHash, contentHash, StringComparison.Ordinal)
            ? draft
            : throw new MatrixRefusedException(MatrixRefusal.DraftChanged);
    }

    internal void Activate(int versionNumber, string contentHash, Guid requestId)
    {
        var draft = RequireActivatable(versionNumber, contentHash);
        CurrentActive?.MarkSuperseded();
        draft.MarkActive(requestId);
    }
}

/// <summary>
/// One numbered version of the matrix: the stored document, its content hash, and the compiled hash (which also covers the
/// attribute versions the conditions used). Active and superseded versions never change (database triggers refuse it).
/// </summary>
public sealed class MatrixVersion
{
    private MatrixVersion()
    {
        Document = string.Empty;
        ContentHash = string.Empty;
        CompiledHash = string.Empty;
        EditedBy = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid MatrixId { get; private set; }

    public int VersionNumber { get; private set; }

    public MatrixVersionState State { get; private set; }

    /// <summary>The canonical stored document (<see cref="MatrixDocuments.ToStored"/>).</summary>
    public string Document { get; private set; }

    public string ContentHash { get; private set; }

    /// <summary>The rules engine's hash of the compiled version, including the attribute versions it used.</summary>
    public string CompiledHash { get; private set; }

    /// <summary>Who wrote this content; they can't be the second person.</summary>
    public string EditedBy { get; private set; }

    public Guid? ActivatedByRequestId { get; private set; }

    public MatrixDocument ToDocument()
    {
        try
        {
            var document = MatrixDocuments.Parse(Document);
            return string.Equals(MatrixDocuments.ToStored(document), Document, StringComparison.Ordinal)
                ? document
                : throw new MatrixRefusedException(MatrixRefusal.InvalidRecord);
        }
        catch (MatrixRejectedException ex)
        {
            throw new MatrixRefusedException(MatrixRefusal.InvalidRecord, ex);
        }
    }

    internal static MatrixVersion CreateDraft(ApprovalMatrix matrix, int versionNumber, MatrixDocument document, string compiledHash, string editor)
    {
        var version = new MatrixVersion { Id = Guid.CreateVersion7(), MatrixId = matrix.Id, VersionNumber = versionNumber, State = MatrixVersionState.Draft };
        version.Apply(document, compiledHash, editor);
        return version;
    }

    internal bool Apply(MatrixDocument document, string compiledHash, string editor)
    {
        if (State != MatrixVersionState.Draft)
        {
            throw new MatrixRefusedException(MatrixRefusal.NoDraft);
        }

        if (!SecondPersonText.IsPerson(editor) || compiledHash is not { Length: 64 })
        {
            throw new MatrixRefusedException(MatrixRefusal.NotAllowed);
        }

        MatrixDocuments.Validate(document);
        var stored = MatrixDocuments.ToStored(document);
        var hash = MatrixDocuments.ComputeContentHash(VersionNumber, stored);
        if (hash == ContentHash && compiledHash == CompiledHash)
        {
            return false;
        }

        Document = stored;
        ContentHash = hash;
        CompiledHash = compiledHash;
        EditedBy = editor;
        return true;
    }

    internal void MarkActive(Guid requestId)
    {
        State = MatrixVersionState.Active;
        ActivatedByRequestId = requestId;
    }

    internal void MarkSuperseded()
    {
        if (State != MatrixVersionState.Active)
        {
            throw new MatrixRefusedException(MatrixRefusal.InvalidRecord);
        }

        State = MatrixVersionState.Superseded;
    }
}

/// <summary>
/// A request to activate one exact draft of the matrix (rule 4): it names the version, content hash and compiled hash, and
/// is decided by a second person who neither made the request nor wrote the draft.
/// </summary>
public sealed class MatrixChangeRequest
{
    private readonly SecondPersonDecision _decision;

    private MatrixChangeRequest(Guid id, Guid matrixId, Guid versionId, int versionNumber, string contentHash, string compiledHash, SecondPersonDecision decision)
    {
        Id = id;
        MatrixId = matrixId;
        VersionId = versionId;
        VersionNumber = versionNumber;
        ContentHash = contentHash;
        CompiledHash = compiledHash;
        _decision = decision;
    }

    public Guid Id { get; }

    public Guid MatrixId { get; }

    public Guid VersionId { get; }

    public int VersionNumber { get; }

    public string ContentHash { get; }

    public string CompiledHash { get; }

    public string Maker => _decision.Maker;

    public string Reason => _decision.Reason;

    public DateTimeOffset ProposedAtUtc => _decision.ProposedAtUtc;

    public SecondPersonState State => _decision.State;

    public string? Checker => _decision.Checker;

    public string? DecisionReason => _decision.DecisionReason;

    public DateTimeOffset? DecidedAtUtc => _decision.DecidedAtUtc;

    /// <summary>Proposes the current draft. The caller has compiled it and found it publishable (no overlaps, DL-090e).</summary>
    public static MatrixChangeRequest Propose(Guid id, ApprovalMatrix matrix, bool publishable, string maker, string? reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (id == Guid.Empty)
        {
            throw new MatrixRefusedException(MatrixRefusal.InvalidRecord);
        }

        var draft = matrix.CurrentDraft ?? throw new MatrixRefusedException(MatrixRefusal.NoDraft);
        if (!publishable)
        {
            throw new MatrixRefusedException(MatrixRefusal.OverlapsFound);
        }

        var decision = Shared(() => SecondPersonDecision.Propose(maker, reason, now));
        return new MatrixChangeRequest(id, matrix.Id, draft.Id, draft.VersionNumber, draft.ContentHash, draft.CompiledHash, decision);
    }

    public static MatrixChangeRequest Restore(
        Guid id, Guid matrixId, Guid versionId, int versionNumber, string contentHash, string compiledHash,
        string maker, string reason, DateTimeOffset proposedAtUtc, SecondPersonState state, string? checker, string? decisionReason, DateTimeOffset? decidedAtUtc)
    {
        if (id == Guid.Empty || matrixId == Guid.Empty || versionId == Guid.Empty || versionNumber < 1 || !IsHash(contentHash) || !IsHash(compiledHash))
        {
            throw new MatrixRefusedException(MatrixRefusal.InvalidRecord);
        }

        try
        {
            return new MatrixChangeRequest(id, matrixId, versionId, versionNumber, contentHash, compiledHash,
                SecondPersonDecision.Restore(maker, reason, proposedAtUtc, state, checker, decisionReason, decidedAtUtc));
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new MatrixRefusedException(MatrixRefusal.InvalidRecord, ex);
        }
    }

    public void RequireDecider(string checker) => Shared(() => _decision.RequireDecider(checker));

    /// <summary>
    /// Approves and activates the named draft. <paramref name="compiledHashNow"/> is the draft compiled again against today's
    /// catalog: if an attribute it uses changed since it was proposed, it is refused.
    /// </summary>
    public void Approve(string checker, string? reason, ApprovalMatrix matrix, string compiledHashNow, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        RequireDecider(checker);
        if (matrix.Id != MatrixId)
        {
            throw new MatrixRefusedException(MatrixRefusal.InvalidRecord);
        }

        var draft = matrix.RequireActivatable(VersionNumber, ContentHash);
        if (string.Equals(draft.EditedBy, checker, StringComparison.Ordinal))
        {
            throw new MatrixRefusedException(MatrixRefusal.MakerCannotDecide);
        }

        if (!string.Equals(draft.CompiledHash, CompiledHash, StringComparison.Ordinal) || !string.Equals(compiledHashNow, CompiledHash, StringComparison.Ordinal))
        {
            throw new MatrixRefusedException(MatrixRefusal.AttributesChanged);
        }

        Shared(() => SecondPersonDecision.RequireOptionalReason(reason));
        Shared(() => _decision.Approve(checker, reason, now));
        matrix.Activate(VersionNumber, ContentHash, Id);
    }

    public void Reject(string checker, string? reason, DateTimeOffset now)
    {
        RequireDecider(checker);
        Shared(() => _decision.Reject(checker, reason, now));
    }

    public void Withdraw(string actor, string? reason, DateTimeOffset now) => Shared(() => _decision.Withdraw(actor, reason, now));

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static T Shared<T>(Func<T> rule)
    {
        try
        {
            return rule();
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new MatrixRefusedException(ex.Refusal switch
            {
                SecondPersonRefusal.NotProposed => MatrixRefusal.NotProposed,
                SecondPersonRefusal.MakerCannotDecide => MatrixRefusal.MakerCannotDecide,
                SecondPersonRefusal.NotMaker => MatrixRefusal.NotMaker,
                SecondPersonRefusal.InvalidReason => MatrixRefusal.InvalidReason,
                SecondPersonRefusal.InvalidPerson => MatrixRefusal.NotAllowed,
                _ => MatrixRefusal.InvalidRecord,
            }, ex);
        }
    }

    private static void Shared(Action rule) => Shared(() =>
    {
        rule();
        return true;
    });
}
