using System.Text.Json;
using System.Text.RegularExpressions;

namespace IAM.Modules.Provisioning.Domain.Items;

/// <summary>
/// Which connector serves an application, and where its new accounts go (DL-093). From the reviewed deployment allowlist,
/// not from data a person can change at run time; an application without one is manual work.
/// </summary>
public sealed record ConnectorBinding(Guid ApplicationId, string ConnectorKey, string AccountContainer)
{
    /// <summary>The connectors this release can run. Only the mock AD connector, which is never production-ready (rule 10).</summary>
    public static readonly IReadOnlySet<string> KnownConnectors = new HashSet<string>(StringComparer.Ordinal) { "mock-ad" };

    /// <summary>The binding if it is well formed, else an exception naming the field (never the value).</summary>
    public static ConnectorBinding Create(Guid applicationId, string? connectorKey, string? accountContainer)
    {
        if (applicationId == Guid.Empty)
        {
            throw new ArgumentException("A connector binding needs an application id.", nameof(applicationId));
        }

        if (connectorKey is null || !KnownConnectors.Contains(connectorKey))
        {
            throw new ArgumentException("A connector binding names an unknown connector.", nameof(connectorKey));
        }

        if (accountContainer is null || accountContainer.Length > 512 || !ProvisioningPlanner.ContainerPattern().IsMatch(accountContainer))
        {
            throw new ArgumentException("A connector binding's account container must be an OU path under DC components.", nameof(accountContainer));
        }

        return new ConnectorBinding(applicationId, connectorKey, accountContainer);
    }
}

/// <summary>The operations planned for an item, in the order they run.</summary>
public sealed record ProvisioningPlan(ProvisioningItem Item, IReadOnlyList<ProvisioningOperation> Operations);

/// <summary>
/// Plans the connector operations for an authorised item (DL-093). A new account is created (disabled, as in AD) in the
/// application's account container, given the entitlement, then enabled; an existing account is given the entitlement. The
/// entitlement's technical name is the target group; the account is the item's. No expressions: fixed steps only (rule 8).
/// </summary>
public static partial class ProvisioningPlanner
{
    // Connector action and object codes (the connector SDK's catalog, DL-080); a test keeps them equal.
    public const string AccountCreate = "account.create";
    public const string AccountEnable = "account.enable";
    public const string EntitlementAdd = "account.entitlement.add";
    public const string AccountObject = "account";
    public const string EntitlementObject = "entitlement";
    public const string ContainerObject = "container";

    /// <summary>Plans a new account: its name must already be checked (DL-092) and the binding is the application's.</summary>
    public static ProvisioningPlan ForNewAccount(Guid itemId, int approvalActions, ItemDetails details, string technicalName, ConnectorBinding binding, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrEmpty(technicalName);
        if (details.AccountId is not null || details.NewAccountType is null || details.AccountName is null || !AccountNamePattern().IsMatch(details.AccountName)
            || details.ApplicationId != binding.ApplicationId)
        {
            throw new ArgumentException("A new account plan needs a valid name and the application's binding.", nameof(details));
        }

        var item = ProvisioningItem.Plan(itemId, approvalActions, details, binding.ConnectorKey, now);
        var account = $"CN={details.AccountName},{binding.AccountContainer}";
        var attributes = JsonSerializer.Serialize(new Dictionary<string, string> { ["sAMAccountName"] = details.AccountName });
        return new ProvisioningPlan(item,
        [
            ProvisioningOperation.Planned(itemId, 1, AccountCreate, AccountObject, account, ContainerObject, binding.AccountContainer, attributes),
            ProvisioningOperation.Planned(itemId, 2, EntitlementAdd, AccountObject, account, EntitlementObject, technicalName, "{}"),
            ProvisioningOperation.Planned(itemId, 3, AccountEnable, AccountObject, account, null, null, "{}"),
        ]);
    }

    /// <summary>Plans access on an existing account, identified on the target by <paramref name="accountTargetId"/>.</summary>
    public static ProvisioningPlan ForExistingAccount(Guid itemId, int approvalActions, ItemDetails details, string accountTargetId, string technicalName, ConnectorBinding binding, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrEmpty(accountTargetId);
        ArgumentException.ThrowIfNullOrEmpty(technicalName);
        if (details.AccountId is null || details.NewAccountType is not null || details.ApplicationId != binding.ApplicationId)
        {
            throw new ArgumentException("An existing account plan needs the account and the application's binding.", nameof(details));
        }

        var item = ProvisioningItem.Plan(itemId, approvalActions, details, binding.ConnectorKey, now);
        return new ProvisioningPlan(item, [ProvisioningOperation.Planned(itemId, 1, EntitlementAdd, AccountObject, accountTargetId, EntitlementObject, technicalName, "{}")]);
    }

    /// <summary>The account name rule (DL-089, DL-092), so a name can sit in a DN without escaping.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,255}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex AccountNamePattern();

    /// <summary>One or more OU components, then one or more DC components, with plain names only.</summary>
    [GeneratedRegex("^(OU=[A-Za-z0-9][A-Za-z0-9 ._-]{0,63},)+(DC=[A-Za-z0-9][A-Za-z0-9-]{0,62},)*DC=[A-Za-z0-9][A-Za-z0-9-]{0,62}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    internal static partial Regex ContainerPattern();
}
