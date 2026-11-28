using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IAM.Connectors.Sdk;

/// <summary>
/// The connector contract (ADR-0007, blueprint §10, DL-080): capabilities, health, read, execute (with read-before-write
/// and idempotency), verify and paged listing for discovery and reconciliation. Executing never throws for a problem in
/// the target or the request: it reports it as a result. A damaged ledger record is thrown (DL-107).
/// </summary>
public interface IConnector
{
    ConnectorCapabilities Capabilities { get; }

    Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>The object as it is now, or null if it doesn't exist.</summary>
    Task<ObjectSnapshot?> ReadAsync(ObjectRef target, CancellationToken cancellationToken);

    Task<OperationResult> ExecuteAsync(ConnectorOperation operation, CancellationToken cancellationToken);

    /// <summary>Whether the target now matches what <paramref name="operation"/> asked for (after the fact, by reading it).</summary>
    Task<VerificationResult> VerifyAsync(ConnectorOperation operation, CancellationToken cancellationToken);

    /// <summary>One page of one object type, from <paramref name="continuation"/> (null for the first page).</summary>
    Task<ObjectPage> ListAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken);
}

/// <summary>What a connector remembers about an operation id: what it asked for (as a fingerprint) and how it ended.</summary>
public sealed record LedgerEntry(Guid OperationId, string Fingerprint, OperationResult Result);

/// <summary>Where a connector remembers the operations it has finished, so a repeat changes nothing twice.</summary>
public interface IOperationLedger
{
    Task<LedgerEntry?> FindAsync(Guid operationId, CancellationToken cancellationToken);

    Task RecordAsync(LedgerEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// A ledger record the SDK can't trust (DL-107): a stored failure, a code only the SDK gives before a write, a status it never
/// records, or a result for another operation. Whether the operation's change was made is unknown, so a person must look; it
/// is thrown, never reported as a result, so no caller can take it for an ordinary failure. Only the SDK creates it (DL-109), and
/// only its replay check lets it out of <see cref="ConnectorBase.ExecuteAsync"/>: from a connector's own code or a ledger it is a
/// connector fault.
/// </summary>
public sealed class LedgerRecordDamagedException : Exception
{
    internal LedgerRecordDamagedException(string message)
        : base(message)
    {
    }
}

/// <summary>An in-memory ledger: for tests and mocks only, lost on restart (DL-080). A real connector keeps one that lasts.</summary>
public sealed class InMemoryOperationLedger : IOperationLedger
{
    private readonly ConcurrentDictionary<Guid, LedgerEntry> _entries = new();

    public int Count => _entries.Count;

    public Task<LedgerEntry?> FindAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.TryGetValue(operationId, out var entry) ? entry : null);

    public Task RecordAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Like a lasting ledger (DL-106): a failure is never remembered, or it would poison the operation id.
        if (entry.Result.Status == OperationStatus.Failed)
        {
            throw new ArgumentException("A failed operation is never recorded.", nameof(entry));
        }

        _entries.TryAdd(entry.OperationId, entry);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The pipeline every connector inherits (DL-080, DL-083), so the safety rules hold whatever the target: an operation is
/// checked against the catalog, the connector's capabilities and its settings; a repeated operation id returns what
/// happened the first time (and is refused if it now asks for something else); a create never takes over an object that
/// already exists; the target is read first, and nothing is changed if it is already as asked; a removal whose account or
/// entitlement is already gone is already done; the change itself is the connector's. Only finished outcomes are
/// remembered, and not those that depend on timing, so a retry runs again under the same id. One operation id, and one
/// operation per target name (compared the connector's way), runs at a time. What a connector returns is checked and
/// filtered to the attributes it declares and may show. Executing never throws for a problem in the target or the request;
/// a ledger record it can't trust is thrown as <see cref="LedgerRecordDamagedException"/> (DL-107).
/// </summary>
public abstract class ConnectorBase(ConnectorSettings settings, IOperationLedger ledger, TimeProvider? clock = null) : IConnector
{
    /// <summary>The most links one snapshot may carry.</summary>
    public const int MaxLinks = 100_000;

    /// <summary>Actions that may carry attributes; any other action carries none.</summary>
    private static readonly HashSet<string> TakesAttributes = new(StringComparer.Ordinal)
    {
        ActionCatalog.AccountCreate, ActionCatalog.AccountUpdate, ActionCatalog.EntitlementCreate,
    };

    /// <summary>Outcomes that depend on when the operation ran: not remembered, so the same id can succeed later.</summary>
    private static readonly HashSet<string> TimingCodes = new(StringComparer.Ordinal)
    {
        ResultCodes.TargetNotFound, ResultCodes.RelatedNotFound,
    };

    private readonly KeyedGates<Guid> _operationGates = new(EqualityComparer<Guid>.Default);
    private KeyedGates<string>? _targetGates;
    private OperationRateLimiter? _rateLimiter;

    public abstract ConnectorCapabilities Capabilities { get; }

    /// <summary>Locks held or awaited now; every one is removed when its last user leaves.</summary>
    internal int ActiveGates => _operationGates.Count + (_targetGates?.Count ?? 0);

    /// <summary>One lock per target name, compared the connector's way, whatever the object type (DL-083b).</summary>
    private KeyedGates<string> TargetGates
    {
        get
        {
            if (Volatile.Read(ref _targetGates) is { } gates)
            {
                return gates;
            }

            Interlocked.CompareExchange(ref _targetGates, new KeyedGates<string>(Capabilities.IdComparer), null);
            return _targetGates!;
        }
    }

    public abstract Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken);

    public async Task<ObjectSnapshot?> ReadAsync(ObjectRef target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsValid || Capabilities.TypeOf(target.ObjectType) is null || !IsValidId(target))
        {
            return null;
        }

        return await ReadCheckedAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> ExecuteAsync(ConnectorOperation operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        LedgerRecordDamagedException? refusal = null;
        try
        {
            return await ExecuteCheckedAsync(operation, e => refusal = e, cancellationToken).ConfigureAwait(false);
        }
        catch (LedgerRecordDamagedException ex) when (!ReferenceEquals(ex, refusal))
        {
            // Not the replay check's own refusal (DL-109): one a connector's code or a ledger threw is a connector fault. Its
            // change may have been made, so it is a failure (an unknown outcome), never damage and never remembered.
            return OperationResult.Of(operation, OperationStatus.Failed, ResultCodes.ConnectorError);
        }
    }

    private async Task<OperationResult> ExecuteCheckedAsync(ConnectorOperation operation, Action<LedgerRecordDamagedException> refuse, CancellationToken cancellationToken)
    {
        if (Check(operation) is { } refused)
        {
            return refused;
        }

        var definition = ActionCatalog.Find(operation.Action)!;
        var fingerprint = Fingerprint(operation);

        // Always the operation's lock first, then its target's: the same order everywhere, so no two can wait on each other.
        using var operationLease = await _operationGates.EnterAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
        using var targetLease = await TargetGates.EnterAsync(operation.Target.Id, cancellationToken).ConfigureAwait(false);

        if (await ledger.FindAsync(operation.OperationId, cancellationToken).ConfigureAwait(false) is { } prior)
        {
            // Whatever the ledger (DL-107): only a finished outcome of this very operation is ever recorded, so anything else is
            // damage, never a result to replay. A replayed failure, or the SDK's own speed limit, would say a change that may
            // have been made never was. Nor is a not-found code ever recorded, or a fingerprint not of the SDK's form (DL-108).
            if (prior.OperationId != operation.OperationId || prior.Result is null || prior.Result.OperationId != operation.OperationId
                || prior.Result.Status == OperationStatus.Failed || !Enum.IsDefined(prior.Result.Status) || prior.Result.Code == ResultCodes.RateLimited
                || (prior.Result.Code is { } priorCode && TimingCodes.Contains(priorCode)) || !IsFingerprint(prior.Fingerprint))
            {
                var damaged = new LedgerRecordDamagedException("The operation ledger returned a record the SDK never records.");
                refuse(damaged);
                throw damaged;
            }

            // Marked as a replay (DL-109): only the SDK says so, and the caller can tell an answer the connector gave now from
            // one a ledger row gave. A row is not proof the change was made: whoever can write the ledger can write one.
            return string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? prior.Result with { Replayed = true }
                : OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.OperationIdReused);
        }

        // Only work that reaches the target counts against the speed limit; a limited operation is retried later. Refused before
        // the connector is called, so nothing was written: the one code that says so is the SDK's alone (DL-104).
        if (!RateLimiter.TryAcquire())
        {
            return OperationResult.Of(operation, OperationStatus.Failed, ResultCodes.RateLimited);
        }

        OperationResult result;
        try
        {
            result = await RunAsync(definition, operation, cancellationToken).ConfigureAwait(false);
        }
        catch (ConnectorTransientException ex)
        {
            result = OperationResult.Of(operation, OperationStatus.Failed, ex.Code);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A connector's own fault: reported, never thrown, and retried like any failure.
            result = OperationResult.Of(operation, OperationStatus.Failed, ResultCodes.ConnectorError);
        }

        // What the connector answers is checked like any ingress (DL-109): an answer for another operation, a status the SDK
        // doesn't know, or none at all came after the connector ran, so it may have written; never remembered, never a replay.
        if (result is null || result.OperationId != operation.OperationId || !Enum.IsDefined(result.Status))
        {
            result = OperationResult.Of(operation, OperationStatus.Failed, ResultCodes.InvalidResponse);
        }

        result = result with { Replayed = false };

        if (result.Code == ResultCodes.RateLimited)
        {
            // The connector ran, so it may have written: it can't claim the SDK's "nothing was written" (DL-104). A success that
            // says so contradicts itself: whether it changed the target is unknown, and it is never remembered (DL-105).
            result = result.Status == OperationStatus.Failed
                ? result with { Code = ResultCodes.ConnectorError }
                : OperationResult.Of(operation, OperationStatus.Failed, ResultCodes.InvalidResponse);
        }

        if (result.Status != OperationStatus.Failed && !(result.Code is { } code && TimingCodes.Contains(code)))
        {
            // Recorded even if the caller gave up meanwhile: the change was made, and a repeat must not make it again.
            await ledger.RecordAsync(new LedgerEntry(operation.OperationId, fingerprint, result), CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<VerificationResult> VerifyAsync(ConnectorOperation operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Check(operation) is { } refused)
        {
            return new VerificationResult(false, refused.Code);
        }

        try
        {
            var current = await ReadCheckedAsync(operation.Target, cancellationToken).ConfigureAwait(false);
            return new VerificationResult(IsInDesiredState(operation, current), null);
        }
        catch (ConnectorTransientException ex)
        {
            // Only the SDK says RateLimited (DL-104); a connector's own is a fault worth retrying, like any other (DL-105).
            return new VerificationResult(false, ex.Code == ResultCodes.RateLimited ? ResultCodes.ConnectorError : ex.Code);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new VerificationResult(false, ResultCodes.ConnectorError);
        }
    }

    public async Task<ObjectPage> ListAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken)
    {
        if (Capabilities.TypeOf(objectType) is not { Discoverable: true })
        {
            throw new ArgumentException("This connector can't discover that object type.", nameof(objectType));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, Capabilities.MaxPageSize);
        if (continuation is not null && !ConnectorText.IsIdentifier(continuation, 4096))
        {
            throw new ArgumentException("The continuation is not one this connector gave out.", nameof(continuation));
        }

        var page = await ListCoreAsync(objectType, continuation, pageSize, cancellationToken).ConfigureAwait(false);
        if (page.Items.Count > pageSize)
        {
            throw new InvalidOperationException("The connector returned more than a page.");
        }

        return page with { Items = [.. page.Items.Select(Sanitize)] };
    }

    /// <summary>
    /// Whether <paramref name="current"/> already is what <paramref name="operation"/> asks for (read before write; also
    /// what verification checks). Identifiers compare the connector's way; an attribute set to null is done when it is
    /// absent or null. A password change or reset has no state to read, so it is never already done: only its operation id
    /// stops it running twice.
    /// </summary>
    protected virtual bool IsInDesiredState(ConnectorOperation operation, ObjectSnapshot? current)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var refs = Capabilities.RefComparer;
        bool Linked(ObjectRef? other) => current is not null && other is not null && current.Links.Contains(other, refs);
        bool HasAttributes() => current is not null
            && operation.Attributes.All(a => current.Attributes.TryGetValue(a.Key, out var value)
                ? string.Equals(value, a.Value, StringComparison.Ordinal)
                : a.Value is null);

        return operation.Action switch
        {
            ActionCatalog.AccountCreate or ActionCatalog.EntitlementCreate or ActionCatalog.AccountUpdate => HasAttributes(),
            ActionCatalog.AccountEnable => current?.Enabled == true,
            ActionCatalog.AccountDisable => current?.Enabled == false,
            ActionCatalog.AccountDelete or ActionCatalog.EntitlementDelete => current is null,
            ActionCatalog.AccountWriteOwner => current?.Owner is { } owner && refs.Equals(owner, operation.Related),
            ActionCatalog.EntitlementAdd or ActionCatalog.MemberAdd => Linked(operation.Related),
            ActionCatalog.EntitlementRemove or ActionCatalog.MemberRemove => current is null || !Linked(operation.Related),
            ActionCatalog.AccountUnlock => current?.Locked == false,
            _ => false,
        };
    }

    /// <summary>The connector's own identifier rules (an AD distinguished name, say), on top of the SDK's shape checks.</summary>
    protected virtual bool IsValidId(ObjectRef reference) => true;

    protected abstract Task<ObjectSnapshot?> ReadCoreAsync(ObjectRef target, CancellationToken cancellationToken);

    /// <summary>Makes the change. Throws <see cref="ConnectorTransientException"/> for a failure worth retrying.</summary>
    protected abstract Task<OperationResult> ApplyAsync(ConnectorOperation operation, ObjectSnapshot? current, CancellationToken cancellationToken);

    protected abstract Task<ObjectPage> ListCoreAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken);

    /// <summary>The lower of the connector's declared speed limit and the application's (DL-082), made on first use.</summary>
    private OperationRateLimiter RateLimiter
    {
        get
        {
            if (Volatile.Read(ref _rateLimiter) is { } limiter)
            {
                return limiter;
            }

            var perSecond = Math.Min(Capabilities.MaxOperationsPerSecond, settings.MaxOperationsPerSecond ?? int.MaxValue);
            Interlocked.CompareExchange(ref _rateLimiter, new OperationRateLimiter(perSecond, clock ?? TimeProvider.System), null);
            return _rateLimiter!;
        }
    }

    /// <summary>A fingerprint of everything an operation asks for, so a reused id that asks for something else is caught.</summary>
    private static string Fingerprint(ConnectorOperation operation)
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            "iam.connector.operation.v1",
            operation.Action,
            operation.Target.ObjectType,
            operation.Target.Id,
            operation.Related?.ObjectType,
            operation.Related?.Id,
            operation.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new[] { a.Key, a.Value }).ToArray(),
            operation.Secret?.Vault,
            operation.Secret?.Name,
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Whether a stored fingerprint has the form <see cref="Fingerprint"/> writes: 64 lower-case hex digits (DL-108).</summary>
    private static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private async Task<ObjectSnapshot?> ReadCheckedAsync(ObjectRef target, CancellationToken cancellationToken) =>
        await ReadCoreAsync(target, cancellationToken).ConfigureAwait(false) is { } snapshot ? Sanitize(snapshot) : null;

    /// <summary>
    /// What a connector returns is ingress too (BACKEND_INPUT_VALIDATION, DL-083c): references must be valid, attributes are
    /// cut down to the declared ones that may be shown, and a link list is bounded. A reference that isn't valid makes the
    /// whole answer untrustworthy, so it is a failure worth retrying, never a quietly shorter list.
    /// </summary>
    private ObjectSnapshot Sanitize(ObjectSnapshot snapshot)
    {
        bool Valid(ObjectRef? r) => r is { IsValid: true } && Capabilities.TypeOf(r.ObjectType) is not null && IsValidId(r);
        if (!Valid(snapshot.Ref) || snapshot.Links.Count > MaxLinks || !snapshot.Links.All(Valid) || (snapshot.Owner is not null && !Valid(snapshot.Owner)))
        {
            throw new ConnectorTransientException(ResultCodes.InvalidResponse);
        }

        var declared = Capabilities.TypeOf(snapshot.Ref.ObjectType)!.Attributes;
        var attributes = snapshot.Attributes
            .Where(a => declared.Contains(a.Key, StringComparer.Ordinal) && !ConnectorText.IsForbiddenAttribute(a.Key) && ConnectorText.IsAttributeValue(a.Value))
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        return snapshot with { Attributes = attributes };
    }

    private async Task<OperationResult> RunAsync(ActionDefinition definition, ConnectorOperation operation, CancellationToken cancellationToken)
    {
        var current = await ReadCheckedAsync(operation.Target, cancellationToken).ConfigureAwait(false);

        // A create never takes over an object that exists, whatever its values (DL-083a): matching an existing account to a
        // person is the platform's correlation, not a side effect of a create.
        if (operation.Action is ActionCatalog.AccountCreate or ActionCatalog.EntitlementCreate)
        {
            return current is not null
                ? OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.AlreadyExists)
                : await ApplyAsync(operation, current, cancellationToken).ConfigureAwait(false);
        }

        var removes = operation.Action is ActionCatalog.EntitlementRemove or ActionCatalog.MemberRemove;
        var deletes = operation.Action is ActionCatalog.AccountDelete or ActionCatalog.EntitlementDelete;
        var related = definition.Related is not null && operation.Related is not null
            ? await ReadCheckedAsync(operation.Related, cancellationToken).ConfigureAwait(false)
            : null;

        // Taking access away from an account or entitlement that is gone: the access is gone too.
        if (removes && (current is null || related is null))
        {
            return OperationResult.Of(operation, OperationStatus.AlreadyInState);
        }

        if (definition.Related is not null && related is null)
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.RelatedNotFound);
        }

        if (current is null && !deletes)
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.TargetNotFound);
        }

        if (IsInDesiredState(operation, current))
        {
            return OperationResult.Of(operation, OperationStatus.AlreadyInState);
        }

        return await ApplyAsync(operation, current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The request's own problems, before anything is read or changed.</summary>
    private OperationResult? Check(ConnectorOperation operation)
    {
        if (ActionCatalog.Find(operation.Action) is not { } definition)
        {
            return OperationResult.Of(operation, OperationStatus.Unsupported, ResultCodes.UnknownAction);
        }

        if (!Capabilities.Supports(definition.Code))
        {
            return OperationResult.Of(operation, OperationStatus.Unsupported, ResultCodes.NotSupported);
        }

        if (!settings.Allows(definition))
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.ActionNotAllowed);
        }

        var shapeOk = operation.OperationId != Guid.Empty && operation.Attributes is not null
            && operation.Target is { IsValid: true } && string.Equals(operation.Target.ObjectType, definition.Target, StringComparison.Ordinal) && IsValidId(operation.Target)
            && (definition.Related is null
                ? operation.Related is null
                : operation.Related is { IsValid: true } related && string.Equals(related.ObjectType, definition.Related, StringComparison.Ordinal) && IsValidId(related));
        if (!shapeOk)
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.InvalidOperation);
        }

        var attributes = operation.Attributes!;
        if (attributes.Keys.Any(ConnectorText.LooksLikeCredential))
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.CredentialInAttributes);
        }

        if (attributes.Keys.Any(ConnectorText.IsForbiddenAttribute))
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.ForbiddenAttribute);
        }

        var declared = Capabilities.TypeOf(definition.Target)?.Attributes ?? [];
        if ((attributes.Count > 0 && !TakesAttributes.Contains(definition.Code))
            || attributes.Count > ConnectorOperation.MaxAttributes
            || attributes.Any(a => !ConnectorText.IsAttributeName(a.Key) || !declared.Contains(a.Key, StringComparer.Ordinal) || !ConnectorText.IsAttributeValue(a.Value)))
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.AttributeNotAllowed);
        }

        if (definition.NeedsSecretReference && operation.Secret is not { IsValid: true })
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.SecretReferenceRequired);
        }

        if (definition.NeedsSecretReference && !settings.AllowsSecret(operation.Secret!))
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.SecretOutsideAllowlist);
        }

        if (!definition.NeedsSecretReference && operation.Secret is not null)
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.SecretReferenceNotAllowed);
        }

        return null;
    }
}

/// <summary>
/// One lock per key, held only while someone uses it (DL-083): the last user to leave removes it, so a long-running
/// connector doesn't keep a lock for every operation and object it has ever seen.
/// </summary>
internal sealed class KeyedGates<TKey>(IEqualityComparer<TKey> comparer)
    where TKey : notnull
{
    private readonly object _sync = new();
    private readonly Dictionary<TKey, Entry> _entries = new(comparer);

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public async Task<IDisposable> EnterAsync(TKey key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(key, entry, release: false);
            throw;
        }

        return new Lease(this, key, entry);
    }

    private void Leave(TKey key, Entry entry, bool release)
    {
        if (release)
        {
            entry.Gate.Release();
        }

        lock (_sync)
        {
            if (--entry.Users == 0)
            {
                _entries.Remove(key);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Lease(KeyedGates<TKey> owner, TKey key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Leave(key, entry, release: true);
            }
        }
    }
}
