using System.Security.Cryptography;
using System.Text;

namespace IAM.Modules.Request.Contracts;

/// <summary>
/// A frozen request item as provisioning sees it (DL-093): who gets which entitlement version, on which account, the new
/// account's name (the one the approval recorded, DL-099), the item's correlation id (DL-094, DL-099), and whether any workflow
/// level gave a name at all (DL-100). Ids and codes only.
/// </summary>
public sealed record ProvisionableItem(
    Guid ItemId,
    string Requester,
    string Beneficiary,
    Guid ApplicationId,
    Guid EntitlementId,
    Guid EntitlementVersionId,
    string EntitlementContentHash,
    Guid? AccountId,
    string? NewAccountType,
    string? AccountName,
    string? CorrelationId = null,
    bool NameGiven = false)
{
    /// <summary>The item id only: logging this object must not expose a person or a login.</summary>
    public override string ToString() => $"{nameof(ProvisionableItem)} {ItemId}";
}

/// <summary>An item's new-account name as it stands: the latest assignment's id (null for the naming rule's proposal) and the name.</summary>
public sealed record AccountNameSnapshot(Guid? AssignmentId, string Name)
{
    /// <summary>The assignment id only: logging this object must not expose a login.</summary>
    public override string ToString() => $"{nameof(AccountNameSnapshot)} {AssignmentId}";
}

/// <summary>The name an approval recorded (DL-099): which assignment (null for the proposal) and the name's hash.</summary>
public sealed record ApprovedAccountName(Guid? AssignmentId, string NameHash);

/// <summary>The SHA-256 of an account name, lower-case hex, as Workflow records it and Provisioning checks it (DL-099).</summary>
public static class AccountNameHash
{
    public static string Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
    }
}

/// <summary>Read-only view of frozen request items for provisioning (ADR-0001: no cross-module table access).</summary>
public interface IRequestItemSource
{
    /// <summary>
    /// The frozen item, or null if there is none. Its account name is the one the approval recorded (DL-099): the assignment
    /// <paramref name="approvedName"/> names (null if it isn't this item's), or the naming rule's proposal when it names none or
    /// there is no recorded name. Names given on a level count only as recorded when it was completed; the caller checks the hash.
    /// </summary>
    Task<ProvisionableItem?> FindAsync(Guid itemId, ApprovedAccountName? approvedName, CancellationToken cancellationToken);

    /// <summary>
    /// The new account's name as it stands now (the latest assignment, else the naming rule's proposal), for Workflow to record
    /// when an Action level is completed (DL-099). Null if there is no such item, it doesn't ask for a new account, or it has no name yet.
    /// </summary>
    Task<AccountNameSnapshot?> CurrentAccountNameAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// What the item was submitted for approval with, rebuilt from the frozen item (DL-096): the people, the matrix evidence
    /// and the route. Workflow starts an approval only from this, never from a message's own copy; provisioning compares the
    /// approval it replays with it. Null if there is no such item.
    /// </summary>
    Task<RequestItemSubmitted?> FindSubmittedAsync(Guid itemId, CancellationToken cancellationToken);
}
