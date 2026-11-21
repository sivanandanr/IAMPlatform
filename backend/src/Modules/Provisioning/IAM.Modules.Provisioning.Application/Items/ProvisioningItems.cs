namespace IAM.Modules.Provisioning.Application.Items;

/// <summary>One step of an item as the IAM team sees it (DL-094): codes and object ids, never a connector's message.</summary>
public sealed record ProvisioningStepView(int Step, string Action, string TargetId, string? RelatedId, string State, string? ResultCode, DateTimeOffset? CompletedAtUtc);

/// <summary>An item as the IAM team sees it (DL-094).</summary>
public sealed record ProvisioningItemView(
    Guid Id,
    string State,
    string? Problem,
    string? IntegrityProblem,
    string? Beneficiary,
    Guid? ApplicationId,
    Guid? EntitlementId,
    Guid? AccountId,
    string? NewAccountType,
    string? AccountName,
    string? ConnectorKey,
    int Attempts,
    Guid? RecordedAccountId,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    IReadOnlyList<ProvisioningStepView> Steps)
{
    /// <summary>The id and state only: logging this object must not expose a person or a login.</summary>
    public override string ToString() => $"{nameof(ProvisioningItemView)} {Id} {State}";
}

/// <summary>A page of items, newest first.</summary>
public sealed record ProvisioningItemPage(IReadOnlyList<ProvisioningItemView> Items, int Page, int PageSize);

/// <summary>Reading items is refused: the caller lacks <c>provisioning.items.read</c>. Nothing about the item is told.</summary>
public sealed class ProvisioningAccessDeniedException : Exception
{
    public ProvisioningAccessDeniedException()
        : base("Not allowed.")
    {
    }

    public ProvisioningAccessDeniedException(string message)
        : base(message)
    {
    }

    public ProvisioningAccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// What the IAM team reads about provisioning (DL-094): blocked and failed items are their tasks. Read only, behind
/// <c>provisioning.items.read</c>, checked here as well as at the endpoint. Resolving items comes later, with a second person.
/// </summary>
public interface IProvisioningItems
{
    const int MaxPageSize = 100;
    const int MaxPage = 1_000;

    /// <summary>Items in <paramref name="state"/> (Blocked or Failed), newest first.</summary>
    Task<ProvisioningItemPage> ListAsync(string state, int page, int pageSize, CancellationToken cancellationToken);

    Task<ProvisioningItemView?> FindAsync(Guid id, CancellationToken cancellationToken);
}
