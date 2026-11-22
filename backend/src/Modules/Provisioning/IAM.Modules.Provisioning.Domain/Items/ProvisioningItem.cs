using System.Security.Cryptography;
using System.Text;

namespace IAM.Modules.Provisioning.Domain.Items;

/// <summary>
/// Where an item or an operation stands (DL-093). Stored as text; never rename a published one. States only move forward:
/// Planned to Running, Running back to Planned (a retry) or on to Succeeded or Failed. Blocked never changes.
/// </summary>
public enum ProvisioningState
{
    Planned = 1,
    Blocked = 2,
    Running = 3,
    Succeeded = 4,
    Failed = 5,
}

/// <summary>Why an authorised item was not planned (DL-093). Codes only: a person resolves it, and nothing is granted meanwhile.</summary>
public enum BlockReason
{
    /// <summary>The approval's log doesn't replay, or doesn't match the audit store (R-037).</summary>
    Integrity = 1,

    /// <summary>The verified approval doesn't authorise provisioning.</summary>
    NotAuthorized = 2,

    /// <summary>The request item isn't there.</summary>
    ItemMissing = 3,

    /// <summary>The request item and the approval don't name the same item or people.</summary>
    ItemMismatch = 4,

    /// <summary>The entitlement's active version isn't the one that was requested and approved.</summary>
    EntitlementChanged = 5,

    /// <summary>The existing account is gone, closed, someone else's, in another application, or not linked to the target.</summary>
    AccountChanged = 6,

    /// <summary>A new account has no name (none proposed, none given on an Action stage, DL-092).</summary>
    NoAccountName = 7,

    /// <summary>A live account of the application has the new account's name now (DL-086).</summary>
    NameInUse = 8,

    /// <summary>No connector serves the application: the work is manual.</summary>
    ManualWork = 9,

    /// <summary>The new account's target id is taken, or the person already has a live Primary account (DL-096).</summary>
    AccountTaken = 10,

    /// <summary>The entitlement's group isn't in the application's allowed group container (DL-098).</summary>
    EntitlementOutOfScope = 11,

    /// <summary>The new account's name isn't the one the approval recorded when its Action level was completed (DL-099).</summary>
    NameNotApproved = 12,
}

/// <summary>The account and entitlement a planned item acts on (DL-093). For a new account, the account fields are its name.</summary>
public sealed record ItemDetails(
    string Beneficiary,
    Guid ApplicationId,
    Guid EntitlementId,
    Guid EntitlementVersionId,
    Guid? AccountId,
    string? NewAccountType,
    string? AccountName)
{
    /// <summary>The ids only: logging this object must not expose a person or a login.</summary>
    public override string ToString() => $"{nameof(ItemDetails)} {ApplicationId} {EntitlementId}";
}

/// <summary>
/// One authorised request item in provisioning (DL-093). Its id is the request item's (and the approval's), so an
/// authorisation delivered twice plans once. Either planned, with its operations, or blocked with a reason; the facts it
/// was planned from never change afterwards (the migrations refuse it), only its state, forward.
/// </summary>
public sealed class ProvisioningItem
{
    public Guid Id { get; private set; }

    /// <summary>
    /// How many actions the verified approval had when this was decided: the log it was planned (or blocked) from. Null only
    /// for an integrity block, whose log isn't trusted.
    /// </summary>
    public int? ApprovalActions { get; private set; }

    public ProvisioningState State { get; private set; }

    /// <summary>The block reason, or later the failure code; null while planned.</summary>
    public string? Problem { get; private set; }

    /// <summary>The R-037 check's problem code when blocked for integrity, else null.</summary>
    public string? IntegrityProblem { get; private set; }

    public string? Beneficiary { get; private set; }

    public Guid? ApplicationId { get; private set; }

    public Guid? EntitlementId { get; private set; }

    public Guid? EntitlementVersionId { get; private set; }

    public Guid? AccountId { get; private set; }

    public string? NewAccountType { get; private set; }

    public string? AccountName { get; private set; }

    /// <summary>The connector that will run the operations, from the reviewed allowlist; null when blocked.</summary>
    public string? ConnectorKey { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    /// <summary>The request's trace id (DL-094), carried into everything the runner audits; set once, when planned or blocked.</summary>
    public string? CorrelationId { get; private set; }

    /// <summary>How many times running it failed for now and was put back for later (DL-094).</summary>
    public int Attempts { get; private set; }

    /// <summary>Not run again before this, after a temporary failure.</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    /// <summary>While running, the runner that claimed it owns it until this; after it, another may take it over.</summary>
    public DateTimeOffset? LeaseUntilUtc { get; private set; }

    /// <summary>The account the Account module recorded for a new account, once the target confirmed it.</summary>
    public Guid? RecordedAccountId { get; private set; }

    public DateTimeOffset? FinishedAtUtc { get; private set; }

    /// <summary>How many times an undo of this item was started or resumed (DL-100); it stops as UndoFailed at the limit.</summary>
    public int UndoAttempts { get; private set; }

    /// <summary>Why this item is being undone (DL-100): the failure it ends with once the undo is confirmed.</summary>
    public string? UndoReason { get; private set; }

    /// <summary>How many times a run was put off because the approval was still being written (DL-100).</summary>
    public int Postponements { get; private set; }

    /// <summary>A code to store as the problem: the connector's if it is one, else <paramref name="fallback"/>.</summary>
    public static string ProblemCode(string? code, string fallback) =>
        code is { Length: > 0 and <= 32 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? code : fallback;

    /// <summary>Records the request's trace id, once.</summary>
    public void Correlate(string correlationId)
    {
        if (CorrelationId is not null || correlationId is not { Length: 32 } || !correlationId.All(char.IsAsciiHexDigitLower) || correlationId.All(c => c == '0'))
        {
            throw new InvalidOperationException("An item takes one trace id, once.");
        }

        CorrelationId = correlationId;
    }

    /// <summary>Whether a runner may take it now: planned and due, or running with an expired lease (its runner stopped).</summary>
    public bool CanClaim(DateTimeOffset now) =>
        (State == ProvisioningState.Planned && (NextAttemptAtUtc is null || NextAttemptAtUtc <= now))
        || (State == ProvisioningState.Running && LeaseUntilUtc < now);

    /// <summary>
    /// Takes the item for a run. Taking over a run whose lease ran out counts as an attempt (DL-097): a run that keeps stopping
    /// without finishing ends like any other repeated failure, instead of being retried for ever.
    /// </summary>
    public void Claim(DateTimeOffset now, TimeSpan lease)
    {
        if (!CanClaim(now) || lease <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The item can't be claimed now.");
        }

        if (State == ProvisioningState.Running)
        {
            Attempts++;
        }

        State = ProvisioningState.Running;
        LeaseUntilUtc = now + lease;
    }

    /// <summary>Extends the running item's lease before its next step (DL-097), so a long run isn't taken over mid-way.</summary>
    public void Renew(DateTimeOffset now, TimeSpan lease)
    {
        RequireRunning();
        LeaseUntilUtc = now + lease;
    }

    /// <summary>
    /// Back to planned, not before <paramref name="next"/>, without counting an attempt: nothing was tried, because the approval
    /// was still being written when it was checked (DL-099).
    /// </summary>
    public void Postpone(DateTimeOffset next)
    {
        RequireRunning();
        Postponements++;
        State = ProvisioningState.Planned;
        NextAttemptAtUtc = next;
        LeaseUntilUtc = null;
    }

    /// <summary>Starts (or resumes) an undo for <paramref name="reason"/> (DL-100); the first reason is kept. Counts one undo pass.</summary>
    public void BeginUndoPass(string reason)
    {
        RequireRunning();
        UndoReason ??= ProblemCode(reason, "Undo");
        UndoAttempts++;
    }

    /// <summary>An undo that failed for now (DL-100): back to planned, not before <paramref name="next"/>; the next run resumes it.</summary>
    public void RetryUndoAt(DateTimeOffset next)
    {
        RequireRunning();
        State = ProvisioningState.Planned;
        NextAttemptAtUtc = next;
        LeaseUntilUtc = null;
    }

    /// <summary>A temporary failure: back to planned, not before <paramref name="next"/>.</summary>
    public void RetryAt(DateTimeOffset next)
    {
        RequireRunning();
        Attempts++;
        State = ProvisioningState.Planned;
        NextAttemptAtUtc = next;
        LeaseUntilUtc = null;
    }

    public void Fail(string code, DateTimeOffset now)
    {
        RequireRunning();
        ArgumentException.ThrowIfNullOrEmpty(code);
        State = ProvisioningState.Failed;
        Problem = code;
        LeaseUntilUtc = null;
        FinishedAtUtc = now;
    }

    public void Succeed(Guid? recordedAccountId, DateTimeOffset now)
    {
        RequireRunning();
        if ((NewAccountType is not null) != (recordedAccountId is not null))
        {
            throw new InvalidOperationException("A new account succeeds only once it is recorded; an existing one records nothing.");
        }

        State = ProvisioningState.Succeeded;
        RecordedAccountId = recordedAccountId;
        LeaseUntilUtc = null;
        FinishedAtUtc = now;
    }

    private void RequireRunning()
    {
        if (State != ProvisioningState.Running)
        {
            throw new InvalidOperationException("Only a running item moves on.");
        }
    }

    public static ProvisioningItem Block(Guid itemId, int? approvalActions, BlockReason reason, string? integrityProblem, ItemDetails? details, DateTimeOffset now)
    {
        var integrity = reason == BlockReason.Integrity;
        if (itemId == Guid.Empty || !Enum.IsDefined(reason) || integrity != (integrityProblem is not null) || integrity != (approvalActions is null)
            || approvalActions < 1)
        {
            throw new ArgumentException("An integrity block, and only one, carries the check's problem and no action count.", nameof(reason));
        }

        var item = New(itemId, approvalActions, ProvisioningState.Blocked, details, now);
        item.Problem = reason.ToString();
        item.IntegrityProblem = integrityProblem;
        return item;
    }

    public static ProvisioningItem Plan(Guid itemId, int approvalActions, ItemDetails details, string connectorKey, DateTimeOffset now)
    {
        if (itemId == Guid.Empty || approvalActions < 1)
        {
            throw new ArgumentException("An item needs its id and the verified approval's action count.", nameof(itemId));
        }

        ArgumentNullException.ThrowIfNull(details);
        ArgumentException.ThrowIfNullOrEmpty(connectorKey);
        var item = New(itemId, approvalActions, ProvisioningState.Planned, details, now);
        item.ConnectorKey = connectorKey;
        return item;
    }

    private static ProvisioningItem New(Guid itemId, int? approvalActions, ProvisioningState state, ItemDetails? details, DateTimeOffset now) => new()
    {
        Id = itemId,
        ApprovalActions = approvalActions,
        State = state,
        Beneficiary = details?.Beneficiary,
        ApplicationId = details?.ApplicationId,
        EntitlementId = details?.EntitlementId,
        EntitlementVersionId = details?.EntitlementVersionId,
        AccountId = details?.AccountId,
        NewAccountType = details?.NewAccountType,
        AccountName = details?.AccountName,
        ReceivedAtUtc = now,
    };
}

/// <summary>
/// One step a connector will run for an item (DL-093): what to do to which target object, with its operation id fixed by
/// the item and the step, so running it again under the same id changes nothing twice (DL-080). Never changed afterwards,
/// except its state, forward.
/// </summary>
public sealed class ProvisioningOperation
{
    private ProvisioningOperation()
    {
        Action = TargetType = TargetId = Attributes = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid ItemId { get; private set; }

    /// <summary>1, 2, 3: the order the operations run in.</summary>
    public int Step { get; private set; }

    /// <summary>A connector action code, for example <c>account.create</c>.</summary>
    public string Action { get; private set; }

    public string TargetType { get; private set; }

    public string TargetId { get; private set; }

    public string? RelatedType { get; private set; }

    public string? RelatedId { get; private set; }

    /// <summary>The attributes to write, as a JSON object of strings (names and identifiers only, never a credential).</summary>
    public string Attributes { get; private set; }

    public ProvisioningState State { get; private set; }

    /// <summary>The connector's result code when it finished, if it gave one.</summary>
    public string? ResultCode { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>Starts (or, after a runner stopped, restarts) the step.</summary>
    public void Start()
    {
        if (State is not (ProvisioningState.Planned or ProvisioningState.Running))
        {
            throw new InvalidOperationException("Only a planned or interrupted step starts.");
        }

        State = ProvisioningState.Running;
    }

    public void Complete(string? code, DateTimeOffset now) => Finish(ProvisioningState.Succeeded, code, now);

    public void Fail(string? code, DateTimeOffset now) => Finish(ProvisioningState.Failed, code, now);

    /// <summary>A temporary failure: the step waits to run again under the same operation id.</summary>
    public void Release()
    {
        RequireRunning();
        State = ProvisioningState.Planned;
    }

    private void Finish(ProvisioningState state, string? code, DateTimeOffset now)
    {
        RequireRunning();
        State = state;
        ResultCode = code is null ? null : ProvisioningItem.ProblemCode(code, "Other");
        CompletedAtUtc = now;
    }

    private void RequireRunning()
    {
        if (State != ProvisioningState.Running)
        {
            throw new InvalidOperationException("Only a running step finishes.");
        }
    }

    internal static ProvisioningOperation Planned(Guid itemId, int step, string action, string targetType, string targetId, string? relatedType, string? relatedId, string attributes) => new()
    {
        Id = OperationId(itemId, step),
        ItemId = itemId,
        Step = step,
        Action = action,
        TargetType = targetType,
        TargetId = targetId,
        RelatedType = relatedType,
        RelatedId = relatedId,
        Attributes = attributes,
        State = ProvisioningState.Planned,
    };

    /// <summary>
    /// The operation id of an item's step: a name-based UUID (version 8, RFC 9562) from a SHA-256 of the item id and step, so
    /// the same step always has the same id and a connector's ledger recognises a repeat.
    /// </summary>
    public static Guid OperationId(Guid itemId, int step)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"iam.provisioning.operation.v1|{itemId:D}|{step}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
