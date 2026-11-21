namespace IAM.Modules.Request.Contracts;

/// <summary>
/// A frozen request item as provisioning sees it (DL-093): who gets which entitlement version, on which account, and the new
/// account's current name (the latest one a workflow level gave, else the naming rule's proposal, DL-092). Ids and codes only.
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
    string? AccountName)
{
    /// <summary>The item id only: logging this object must not expose a person or a login.</summary>
    public override string ToString() => $"{nameof(ProvisionableItem)} {ItemId}";
}

/// <summary>Read-only view of frozen request items for provisioning (ADR-0001: no cross-module table access).</summary>
public interface IRequestItemSource
{
    /// <summary>The frozen item, or null if there is none.</summary>
    Task<ProvisionableItem?> FindAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// What the item was submitted for approval with, rebuilt from the frozen item (DL-096): the people, the matrix evidence
    /// and the route. Workflow starts an approval only from this, never from a message's own copy; provisioning compares the
    /// approval it replays with it. Null if there is no such item.
    /// </summary>
    Task<RequestItemSubmitted?> FindSubmittedAsync(Guid itemId, CancellationToken cancellationToken);
}
