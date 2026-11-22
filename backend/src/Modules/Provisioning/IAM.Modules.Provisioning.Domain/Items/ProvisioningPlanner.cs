using System.Text.Json;
using System.Text.RegularExpressions;

namespace IAM.Modules.Provisioning.Domain.Items;

/// <summary>
/// Which connector serves an application, where its new accounts go (DL-093), and the only container its entitlements' groups
/// may be in (DL-098). From the reviewed deployment allowlist, not from data a person can change at run time; an application
/// without one is manual work.
/// </summary>
public sealed record ConnectorBinding(Guid ApplicationId, string ConnectorKey, string AccountContainer, string GroupContainer)
{
    /// <summary>The connectors this release can run. Only the mock AD connector, which is never production-ready (rule 10).</summary>
    public static readonly IReadOnlySet<string> KnownConnectors = new HashSet<string>(StringComparer.Ordinal) { "mock-ad" };

    /// <summary>The binding if it is well formed, else an exception naming the field (never the value).</summary>
    public static ConnectorBinding Create(Guid applicationId, string? connectorKey, string? accountContainer, string? groupContainer)
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

        if (groupContainer is null || groupContainer.Length > 512 || !ProvisioningPlanner.ContainerPattern().IsMatch(groupContainer))
        {
            throw new ArgumentException("A connector binding's group container must be an OU path under DC components.", nameof(groupContainer));
        }

        return new ConnectorBinding(applicationId, connectorKey, accountContainer, groupContainer);
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
    public const string EntitlementRemove = "account.entitlement.remove";
    public const string AccountDisable = "account.disable";

    /// <summary>Steps above this undo a run (DL-096); a plan never has more.</summary>
    public const int MaxPlanSteps = 10;
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
        var account = NewAccountTarget(details.AccountName, binding);
        var attributes = JsonSerializer.Serialize(new Dictionary<string, string> { ["sAMAccountName"] = details.AccountName });
        return new ProvisioningPlan(item,
        [
            ProvisioningOperation.Planned(itemId, 1, AccountCreate, AccountObject, account, ContainerObject, binding.AccountContainer, attributes),
            ProvisioningOperation.Planned(itemId, 2, EntitlementAdd, AccountObject, account, EntitlementObject, technicalName, "{}"),
            ProvisioningOperation.Planned(itemId, 3, AccountEnable, AccountObject, account, null, null, "{}"),
        ]);
    }

    /// <summary>
    /// Whether an entitlement's technical name is a plain group directly in the application's allowed group container (DL-098):
    /// a catalog maker can't point an entitlement at any group in the directory, such as an administrators' group elsewhere.
    /// </summary>
    public static bool IsInGroupContainer(string technicalName, ConnectorBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var suffix = "," + binding.GroupContainer;
        return technicalName is { Length: <= 512 }
            && technicalName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && GroupPattern().IsMatch(technicalName[..^suffix.Length]);
    }

    /// <summary>Where a new account goes: a CN in the application's account container.</summary>
    public static string NewAccountTarget(string accountName, ConnectorBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return $"CN={accountName},{binding.AccountContainer}";
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

    /// <summary>The result code of a step that changed the target itself (the connector's Applied), as against one already in state.</summary>
    public const string Applied = "Applied";

    /// <summary>
    /// What undoes an item that fails after its steps ran (DL-096, DL-099), under fixed operation ids (steps 11 and 12), from the
    /// plan's stored states alone, so a run that stops half-way works out the same undo again:
    /// <list type="bullet">
    /// <item>A new account this item created: its entitlement is removed, then it is disabled. It stays, for a person to look at;
    /// nothing is deleted. A create that didn't succeed may have met someone else's object, which is never touched.</item>
    /// <item>An existing account: the entitlement is removed only if this item's step added it (<see cref="Applied"/>); access the
    /// person already had is never taken away.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<ProvisioningOperation> Undo(Guid itemId, IReadOnlyList<ProvisioningOperation> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // A plan of any other shape was never one the planner made (a step added by SQL): the runner stops it before it runs
        // a step (PlanMismatch), so there is nothing of it to undo.
        var adds = plan.Where(o => o.Action == EntitlementAdd).ToList();
        var creates = plan.Where(o => o.Action == AccountCreate).ToList();
        if (adds.Count > 1 || creates.Count > 1)
        {
            return [];
        }

        var add = adds.SingleOrDefault();
        if (creates.SingleOrDefault() is { } create)
        {
            return create.State != ProvisioningState.Succeeded || add is null
                ? []
                :
                [
                    ProvisioningOperation.Planned(itemId, MaxPlanSteps + 1, EntitlementRemove, AccountObject, create.TargetId, EntitlementObject, add.RelatedId, "{}"),
                    ProvisioningOperation.Planned(itemId, MaxPlanSteps + 2, AccountDisable, AccountObject, create.TargetId, null, null, "{}"),
                ];
        }

        return add is { State: ProvisioningState.Succeeded, ResultCode: Applied }
            ? [ProvisioningOperation.Planned(itemId, MaxPlanSteps + 1, EntitlementRemove, AccountObject, add.TargetId, EntitlementObject, add.RelatedId, "{}")]
            : [];
    }

    /// <summary>Whether two plans are the same steps: ids, actions, objects and attributes (DL-096). Undo steps don't count.</summary>
    public static bool SamePlan(IEnumerable<ProvisioningOperation> a, IEnumerable<ProvisioningOperation> b)
    {
        static string Key(ProvisioningOperation o) =>
            string.Join('\u001f', o.Step, o.Id, o.Action, o.TargetType, o.TargetId, o.RelatedType ?? "-", o.RelatedId ?? "-", o.Attributes);
        var left = a.Where(o => o.Step <= MaxPlanSteps).OrderBy(o => o.Step).Select(Key).ToList();
        var right = b.Where(o => o.Step <= MaxPlanSteps).OrderBy(o => o.Step).Select(Key).ToList();
        return left.Count > 0 && left.SequenceEqual(right, StringComparer.Ordinal);
    }

    /// <summary>The account name rule (DL-089, DL-092), so a name can sit in a DN without escaping.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,255}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex AccountNamePattern();

    /// <summary>A group's own name: one CN with a plain name.</summary>
    [GeneratedRegex("^CN=[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex GroupPattern();

    /// <summary>One or more OU components, then one or more DC components, with plain names only.</summary>
    [GeneratedRegex("^(OU=[A-Za-z0-9][A-Za-z0-9 ._-]{0,63},)+(DC=[A-Za-z0-9][A-Za-z0-9-]{0,62},)*DC=[A-Za-z0-9][A-Za-z0-9-]{0,62}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    internal static partial Regex ContainerPattern();
}
