using System.Text;
using IAM.Connectors.Sdk;

namespace IAM.Connectors.ActiveDirectory.Mock;

/// <summary>
/// <b>MOCK — not production-ready.</b> An in-memory stand-in for Active Directory (P0-011, ADR-0007, DL-081): accounts
/// (users), entitlements (groups) and containers (OUs) under one base DN, with group membership kept on both sides. It
/// implements the whole action catalog through <see cref="ConnectorBase"/>, so the SDK's safety rules hold; it never talks
/// to a real directory, keeps nothing across restarts, and reports itself as not production-ready in its capabilities and
/// health (CLAUDE.md rule 10, FR-REL-103). A new account starts disabled, as in AD. <see cref="Faults"/> and the drift
/// methods simulate a slow, failing or changed directory for tests and demos (DL-082).
/// </summary>
public sealed class MockActiveDirectoryConnector : ConnectorBase
{
    public const string ConnectorType = "activedirectory.mock";
    public const string DefaultBaseDn = "DC=example,DC=test";

    private static readonly ConnectorCapabilities Declared = new(
        ConnectorType,
        ProductionReady: false,
        [.. ActionCatalog.All.Select(a => a.Code)],
        [
            new ObjectTypeCapability(ObjectTypes.Account, Discoverable: true, ["displayName", "givenName", "sn", "mail", "department", "title", "sAMAccountName", "employeeID", "description"]),
            new ObjectTypeCapability(ObjectTypes.Entitlement, Discoverable: true, ["displayName", "description"]),
            new ObjectTypeCapability(ObjectTypes.Container, Discoverable: true, ["description"]),
        ],
        MaxOperationsPerSecond: 50,
        MaxPageSize: 500);

    private readonly object _sync = new();
    private readonly Dictionary<string, Node> _directory = new(StringComparer.Ordinal);
    private readonly string _baseDn;
    private readonly TimeProvider _clock;
    private int _pagesListed;

    public MockActiveDirectoryConnector(ConnectorSettings settings, IOperationLedger ledger, string baseDn = DefaultBaseDn, TimeProvider? clock = null)
        : base(settings, ledger, clock)
    {
        _clock = clock ?? TimeProvider.System;
        if (DistinguishedName.Parse(baseDn) is not { } parts || parts.Any(p => p.Type != "DC"))
        {
            throw new ArgumentException("The base DN must be DC components only.", nameof(baseDn));
        }

        _baseDn = baseDn;
    }

    public override ConnectorCapabilities Capabilities => Declared;

    public string BaseDn => _baseDn;

    /// <summary>Mock only: switches that make the directory slow, failing or unavailable (DL-082).</summary>
    public MockFaults Faults { get; } = new();

    /// <summary>Mock only: changes an attribute outside IAM, as an administrator might (drift).</summary>
    public void DriftAttribute(ObjectRef target, string name, string? value)
    {
        lock (_sync)
        {
            Merge(_directory[target.Id], new Dictionary<string, string?> { [name] = value });
        }
    }

    /// <summary>Mock only: adds a member outside IAM (drift).</summary>
    public void DriftAddMember(ObjectRef group, ObjectRef account)
    {
        lock (_sync)
        {
            _directory[group.Id].Links.Add(account.Id);
            _directory[account.Id].Links.Add(group.Id);
        }
    }

    /// <summary>Mock only: deletes an object outside IAM, leaving no dangling membership (drift).</summary>
    public void DriftRemove(ObjectRef target)
    {
        lock (_sync)
        {
            foreach (var other in _directory[target.Id].Links)
            {
                if (_directory.TryGetValue(other, out var linked))
                {
                    linked.Links.Remove(target.Id);
                }
            }

            _directory.Remove(target.Id);
        }
    }

    /// <summary>Mock only: adds an OU, as an administrator would outside IAM.</summary>
    public ObjectRef AddContainer(string ou) => Add(ObjectTypes.Container, $"OU={ou},{_baseDn}", new Node(ObjectTypes.Container));

    /// <summary>Mock only: adds a group in <paramref name="container"/>, as an administrator would outside IAM.</summary>
    public ObjectRef AddGroup(string name, ObjectRef container) => Add(ObjectTypes.Entitlement, $"CN={name},{container.Id}", new Node(ObjectTypes.Entitlement));

    /// <summary>Mock only: locks an account, as too many failed sign-ins would.</summary>
    public void Lock(ObjectRef account)
    {
        lock (_sync)
        {
            _directory[account.Id].Locked = true;
        }
    }

    /// <summary>Mock only: whether a password reset left the account needing a new password at next sign-in.</summary>
    public bool MustChangePassword(ObjectRef account)
    {
        lock (_sync)
        {
            return _directory[account.Id].MustChangePassword;
        }
    }

    /// <summary>Mock only: how many password changes reached this account (the values themselves never come here).</summary>
    public int PasswordChanges(ObjectRef account)
    {
        lock (_sync)
        {
            return _directory[account.Id].PasswordChanges;
        }
    }

    public override Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Faults.Unavailable
            ? new ConnectorHealth(HealthStatus.Unavailable, "MockUnavailable", ProductionReady: false)
            : new ConnectorHealth(HealthStatus.Healthy, "Mock", ProductionReady: false));

    protected override bool IsValidId(ObjectRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!DistinguishedName.IsValid(reference.Id, _baseDn) || DistinguishedName.Parse(reference.Id) is not { } parts)
        {
            return false;
        }

        // A user or group is a CN in an OU; a container is an OU.
        return reference.ObjectType switch
        {
            ObjectTypes.Account or ObjectTypes.Entitlement => parts[0].Type == "CN" && parts.Count > 1 && parts[1].Type == "OU",
            ObjectTypes.Container => parts[0].Type == "OU",
            _ => false,
        };
    }

    protected override async Task<ObjectSnapshot?> ReadCoreAsync(ObjectRef target, CancellationToken cancellationToken)
    {
        await FaultAsync(cancellationToken).ConfigureAwait(false);
        return await ReadNowAsync(target).ConfigureAwait(false);
    }

    protected override async Task<OperationResult> ApplyAsync(ConnectorOperation operation, ObjectSnapshot? current, CancellationToken cancellationToken)
    {
        await FaultAsync(cancellationToken).ConfigureAwait(false);
        if (Faults.TakeWriteFailure() is { } code)
        {
            throw new ConnectorTransientException(code);
        }

        return await ApplyNowAsync(operation).ConfigureAwait(false);
    }

    protected override async Task<ObjectPage> ListCoreAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken)
    {
        await FaultAsync(cancellationToken).ConfigureAwait(false);
        if (Faults.FailListingAfterPages is { } limit && Interlocked.Increment(ref _pagesListed) > limit)
        {
            throw new ConnectorTransientException(ResultCodes.TargetUnavailable);
        }

        return await ListNowAsync(objectType, continuation, pageSize).ConfigureAwait(false);
    }

    /// <summary>The fault switches that apply to every call: unavailable, then the delay.</summary>
    private async Task FaultAsync(CancellationToken cancellationToken)
    {
        if (Faults.Unavailable)
        {
            throw new ConnectorTransientException(ResultCodes.TargetUnavailable);
        }

        if (Faults.Latency > TimeSpan.Zero)
        {
            await Task.Delay(Faults.Latency, _clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<ObjectSnapshot?> ReadNowAsync(ObjectRef target)
    {
        lock (_sync)
        {
            return Task.FromResult(_directory.TryGetValue(target.Id, out var node) && node.ObjectType == target.ObjectType ? Snapshot(target.Id, node) : null);
        }
    }

    private Task<OperationResult> ApplyNowAsync(ConnectorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_sync)
        {
            var id = operation.Target.Id;
            var related = operation.Related?.Id;
            switch (operation.Action)
            {
                case ActionCatalog.AccountCreate or ActionCatalog.EntitlementCreate:
                    if (!string.Equals(DistinguishedName.Parent(id), related, StringComparison.Ordinal))
                    {
                        // The DN must sit directly in the container named.
                        return Task.FromResult(OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.InvalidOperation));
                    }

                    var created = new Node(operation.Target.ObjectType)
                    {
                        Enabled = operation.Target.ObjectType == ObjectTypes.Account ? false : null,
                        Locked = operation.Target.ObjectType == ObjectTypes.Account ? false : null,
                    };
                    Merge(created, operation.Attributes);
                    _directory[id] = created;
                    break;
                case ActionCatalog.AccountUpdate:
                    Merge(_directory[id], operation.Attributes);
                    break;
                case ActionCatalog.AccountEnable:
                    _directory[id].Enabled = true;
                    break;
                case ActionCatalog.AccountDisable:
                    _directory[id].Enabled = false;
                    break;
                case ActionCatalog.AccountDelete or ActionCatalog.EntitlementDelete:
                    foreach (var other in _directory[id].Links)
                    {
                        if (_directory.TryGetValue(other, out var linked))
                        {
                            linked.Links.Remove(id);
                        }
                    }

                    _directory.Remove(id);
                    break;
                case ActionCatalog.AccountWriteOwner:
                    _directory[id].Owner = related;
                    break;
                case ActionCatalog.EntitlementAdd or ActionCatalog.MemberAdd:
                    _directory[id].Links.Add(related!);
                    _directory[related!].Links.Add(id);
                    break;
                case ActionCatalog.EntitlementRemove or ActionCatalog.MemberRemove:
                    _directory[id].Links.Remove(related!);
                    _directory[related!].Links.Remove(id);
                    break;
                case ActionCatalog.PasswordChange:
                    // The connector would fetch the new password from the vault reference here, inside the customer's
                    // network. The mock has no vault: it only counts the change; nothing about the reference is kept.
                    _directory[id].PasswordChanges++;
                    _directory[id].MustChangePassword = false;
                    break;
                case ActionCatalog.PasswordReset:
                    _directory[id].MustChangePassword = true;
                    break;
                case ActionCatalog.AccountUnlock:
                    _directory[id].Locked = false;
                    break;
                default:
                    return Task.FromResult(OperationResult.Of(operation, OperationStatus.Unsupported, ResultCodes.NotSupported));
            }

            return Task.FromResult(OperationResult.Of(operation, OperationStatus.Applied));
        }
    }

    private Task<ObjectPage> ListNowAsync(string objectType, string? continuation, int pageSize)
    {
        string? after = null;
        if (continuation is not null)
        {
            try
            {
                after = Encoding.UTF8.GetString(Convert.FromBase64String(continuation));
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("The continuation is not one this connector gave out.", nameof(continuation), ex);
            }

            if (!DistinguishedName.IsValid(after, _baseDn))
            {
                throw new ArgumentException("The continuation is not one this connector gave out.", nameof(continuation));
            }
        }

        lock (_sync)
        {
            // Ordered by DN; the next page starts after the last DN given, so changes between pages can't repeat an object.
            var all = _directory.Where(e => e.Value.ObjectType == objectType && (after is null || string.CompareOrdinal(e.Key, after) > 0))
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Take(pageSize + 1)
                .ToList();
            var items = all.Take(pageSize).Select(e => Snapshot(e.Key, e.Value)).ToList();
            var next = all.Count > pageSize ? Convert.ToBase64String(Encoding.UTF8.GetBytes(items[^1].Ref.Id)) : null;
            return Task.FromResult(new ObjectPage(items, next));
        }
    }

    private static void Merge(Node node, IReadOnlyDictionary<string, string?> attributes)
    {
        foreach (var (name, value) in attributes)
        {
            if (value is null)
            {
                node.Attributes.Remove(name);
            }
            else
            {
                node.Attributes[name] = value;
            }
        }
    }

    private ObjectRef Add(string objectType, string dn, Node node)
    {
        var reference = new ObjectRef(objectType, dn);
        if (!reference.IsValid || !IsValidId(reference))
        {
            throw new ArgumentException("Not a name the mock accepts.", nameof(dn));
        }

        lock (_sync)
        {
            _directory.TryAdd(dn, node);
        }

        return reference;
    }

    private static ObjectSnapshot Snapshot(string dn, Node node)
    {
        var linkType = node.ObjectType == ObjectTypes.Account ? ObjectTypes.Entitlement : ObjectTypes.Account;
        return new ObjectSnapshot(
            new ObjectRef(node.ObjectType, dn),
            node.Enabled,
            node.Locked,
            new Dictionary<string, string?>(node.Attributes, StringComparer.Ordinal),
            [.. node.Links.Order(StringComparer.Ordinal).Select(l => new ObjectRef(linkType, l))],
            node.Owner is { } owner ? new ObjectRef(ObjectTypes.Account, owner) : null);
    }

    private sealed class Node(string objectType)
    {
        public string ObjectType { get; } = objectType;

        public bool? Enabled { get; set; }

        public bool? Locked { get; set; }

        public Dictionary<string, string?> Attributes { get; } = new(StringComparer.Ordinal);

        /// <summary>An account's groups, or a group's members, by DN.</summary>
        public HashSet<string> Links { get; } = new(StringComparer.Ordinal);

        public string? Owner { get; set; }

        public bool MustChangePassword { get; set; }

        public int PasswordChanges { get; set; }
    }
}

/// <summary>
/// <b>Mock only.</b> Switches that make <see cref="MockActiveDirectoryConnector"/> slow, failing or unavailable, to test what
/// callers do when a real directory misbehaves (DL-082). Failures are the transient kind: the SDK reports them as Failed
/// and doesn't remember them, so a retry under the same operation id runs again.
/// </summary>
public sealed class MockFaults
{
    private int _failNextWrites;

    /// <summary>Every read, write and listing waits this long first (on the connector's clock).</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>Health reports Unavailable and every read, write and listing fails.</summary>
    public bool Unavailable { get; set; }

    /// <summary>The code the next failing writes report: <c>Throttled</c>, <c>TargetUnavailable</c> or <c>TimedOut</c>.</summary>
    public string FailureCode { get; set; } = ResultCodes.TargetUnavailable;

    /// <summary>Listing fails once this many pages have been served since the connector was made.</summary>
    public int? FailListingAfterPages { get; set; }

    /// <summary>The next <paramref name="count"/> writes fail with <see cref="FailureCode"/>.</summary>
    public void FailNextWrites(int count) => Interlocked.Exchange(ref _failNextWrites, Math.Max(count, 0));

    internal string? TakeWriteFailure()
    {
        while (true)
        {
            var left = Volatile.Read(ref _failNextWrites);
            if (left <= 0)
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref _failNextWrites, left - 1, left) == left)
            {
                return FailureCode;
            }
        }
    }
}
