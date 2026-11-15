using System.Collections.Concurrent;
using System.Globalization;
using IAM.Connectors.Sdk;

namespace IAM.Connectors.Tests;

/// <summary>
/// A deliberately small connector for testing the SDK and the contract suite: accounts, entitlements on an account and
/// password changes only, over a dictionary. It exists to prove the suite skips what a connector doesn't support and holds
/// whatever a connector does. Not a mock of any real system (that is slice 2's mock AD connector).
/// </summary>
internal sealed class MinimalTestConnector(ConnectorSettings settings, IOperationLedger ledger) : ConnectorBase(settings, ledger)
{
    public static readonly ObjectRef Container = new(ObjectTypes.Container, "root");
    public static readonly ObjectRef Group = new(ObjectTypes.Entitlement, "group-a");

    private readonly ConcurrentDictionary<ObjectRef, Entry> _objects = new(new[]
    {
        KeyValuePair.Create(Container, new Entry(null, null, new Dictionary<string, string?>(), [])),
        KeyValuePair.Create(Group, new Entry(null, null, new Dictionary<string, string?>(), [])),
    });

    public int Writes { get; private set; }

    public override ConnectorCapabilities Capabilities { get; } = new(
        "test.minimal",
        ProductionReady: false,
        [
            ActionCatalog.AccountCreate, ActionCatalog.AccountUpdate, ActionCatalog.AccountEnable, ActionCatalog.AccountDisable,
            ActionCatalog.AccountDelete, ActionCatalog.EntitlementAdd, ActionCatalog.EntitlementRemove, ActionCatalog.PasswordChange,
        ],
        [
            new ObjectTypeCapability(ObjectTypes.Account, Discoverable: true, ["displayName", "department"]),
            new ObjectTypeCapability(ObjectTypes.Entitlement, Discoverable: false, ["name"]),
            new ObjectTypeCapability(ObjectTypes.Container, Discoverable: false, []),
        ],
        MaxOperationsPerSecond: 100,
        MaxPageSize: 50);

    public override Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ConnectorHealth(HealthStatus.Healthy, "Test", ProductionReady: false));

    protected override Task<ObjectSnapshot?> ReadCoreAsync(ObjectRef target, CancellationToken cancellationToken) =>
        Task.FromResult(_objects.TryGetValue(target, out var entry) ? Snapshot(target, entry) : null);

    protected override Task<OperationResult> ApplyAsync(ConnectorOperation operation, ObjectSnapshot? current, CancellationToken cancellationToken)
    {
        Writes++;
        var target = operation.Target;
        switch (operation.Action)
        {
            case ActionCatalog.AccountCreate:
                _objects[target] = new Entry(true, false, new Dictionary<string, string?>(operation.Attributes), []);
                break;
            case ActionCatalog.AccountUpdate:
                _objects[target] = _objects[target] with { Attributes = new Dictionary<string, string?>(_objects[target].Attributes.Concat(operation.Attributes).GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.Last().Value)) };
                break;
            case ActionCatalog.AccountEnable or ActionCatalog.AccountDisable:
                _objects[target] = _objects[target] with { Enabled = operation.Action == ActionCatalog.AccountEnable };
                break;
            case ActionCatalog.AccountDelete:
                _objects.TryRemove(target, out _);
                break;
            case ActionCatalog.EntitlementAdd:
                _objects[target] = _objects[target] with { Links = [.. _objects[target].Links, operation.Related!] };
                break;
            case ActionCatalog.EntitlementRemove:
                _objects[target] = _objects[target] with { Links = [.. _objects[target].Links.Where(l => l != operation.Related)] };
                break;
            case ActionCatalog.PasswordChange:
                // The reference would be fetched from the vault here; nothing about it is stored or shown.
                break;
        }

        return Task.FromResult(OperationResult.Of(operation, OperationStatus.Applied));
    }

    protected override Task<ObjectPage> ListCoreAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken)
    {
        var start = continuation is null ? 0 : int.Parse(continuation, CultureInfo.InvariantCulture);
        var all = _objects.Where(o => o.Key.ObjectType == objectType).OrderBy(o => o.Key.Id, StringComparer.Ordinal).ToList();
        var items = all.Skip(start).Take(pageSize).Select(o => Snapshot(o.Key, o.Value)).ToList();
        var next = start + items.Count < all.Count ? (start + items.Count).ToString(CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new ObjectPage(items, next));
    }

    private static ObjectSnapshot Snapshot(ObjectRef target, Entry entry) =>
        new(target, entry.Enabled, entry.Locked, entry.Attributes, entry.Links, null);

    private sealed record Entry(bool? Enabled, bool? Locked, IReadOnlyDictionary<string, string?> Attributes, IReadOnlyList<ObjectRef> Links);
}

/// <summary>The contract suite run against the minimal test connector: it passes, and skips what that connector doesn't support.</summary>
public sealed class MinimalTestConnectorContractTests : ConnectorContractTests
{
    protected override ObjectRef Container => MinimalTestConnector.Container;

    protected override ObjectRef ExistingEntitlement => MinimalTestConnector.Group;

    protected override IConnector Create(ConnectorSettings settings) => new MinimalTestConnector(settings, new InMemoryOperationLedger());

    protected override ObjectRef NewAccount() => new(ObjectTypes.Account, $"user-{Guid.CreateVersion7():N}");
}
