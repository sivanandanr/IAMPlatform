using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IAM.Connectors.Sdk;

/// <summary>
/// The connector contract (ADR-0007, blueprint §10, DL-080): capabilities, health, read, execute (with read-before-write
/// and idempotency), verify and paged listing for discovery and reconciliation. Executing never throws for a problem in
/// the target or the request: it reports it as a result.
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
        _entries.TryAdd(entry.OperationId, entry);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The pipeline every connector inherits (DL-080), so the safety rules hold whatever the target: an operation is checked
/// against the catalog, the connector's capabilities and its settings; a repeated operation id returns what happened the
/// first time (and is refused if it now asks for something else); the target is read first, and nothing is changed if it
/// is already as asked; the change itself is the connector's. Only finished outcomes are remembered, so a failure worth
/// retrying runs again under the same id. One operation id, and one operation per target object, runs at a time, so two
/// requests for the same change can't both read "not done yet" and both make it.
/// </summary>
public abstract class ConnectorBase(ConnectorSettings settings, IOperationLedger ledger) : IConnector
{
    /// <summary>Actions that may carry attributes; any other action carries none.</summary>
    private static readonly HashSet<string> TakesAttributes = new(StringComparer.Ordinal)
    {
        ActionCatalog.AccountCreate, ActionCatalog.AccountUpdate, ActionCatalog.EntitlementCreate,
    };

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _operationGates = new();
    private readonly ConcurrentDictionary<ObjectRef, SemaphoreSlim> _targetGates = new();

    public abstract ConnectorCapabilities Capabilities { get; }

    public abstract Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken);

    public async Task<ObjectSnapshot?> ReadAsync(ObjectRef target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsValid || Capabilities.TypeOf(target.ObjectType) is null || !IsValidId(target))
        {
            return null;
        }

        return await ReadCoreAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> ExecuteAsync(ConnectorOperation operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Check(operation) is { } refused)
        {
            return refused;
        }

        var definition = ActionCatalog.Find(operation.Action)!;
        var fingerprint = Fingerprint(operation);
        // Always the operation's gate first, then its target's: the same order everywhere, so no two can wait on each other.
        var operationGate = _operationGates.GetOrAdd(operation.OperationId, _ => new SemaphoreSlim(1, 1));
        var targetGate = _targetGates.GetOrAdd(operation.Target, _ => new SemaphoreSlim(1, 1));
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            operationGate.Release();
            throw;
        }

        try
        {
            if (await ledger.FindAsync(operation.OperationId, cancellationToken).ConfigureAwait(false) is { } prior)
            {
                return string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? prior.Result
                    : OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.OperationIdReused);
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

            if (result.Status != OperationStatus.Failed)
            {
                await ledger.RecordAsync(new LedgerEntry(operation.OperationId, fingerprint, result), cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            targetGate.Release();
            operationGate.Release();
        }
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
            var current = await ReadCoreAsync(operation.Target, cancellationToken).ConfigureAwait(false);
            return new VerificationResult(IsInDesiredState(operation, current), null);
        }
        catch (ConnectorTransientException ex)
        {
            return new VerificationResult(false, ex.Code);
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
        if (continuation is not null && !ConnectorText.IsIdentifier(continuation, 1024))
        {
            throw new ArgumentException("The continuation is not one this connector gave out.", nameof(continuation));
        }

        var page = await ListCoreAsync(objectType, continuation, pageSize, cancellationToken).ConfigureAwait(false);
        return page.Items.Count <= pageSize ? page : throw new InvalidOperationException("The connector returned more than a page.");
    }

    /// <summary>
    /// Whether <paramref name="current"/> already is what <paramref name="operation"/> asks for (read before write). The
    /// default covers the whole catalog from the snapshot; a password change or reset has no state to read, so it is never
    /// already done: only its operation id stops it running twice.
    /// </summary>
    protected virtual bool IsInDesiredState(ConnectorOperation operation, ObjectSnapshot? current)
    {
        ArgumentNullException.ThrowIfNull(operation);
        bool Linked(ObjectRef? other) => current is not null && other is not null && current.Links.Contains(other);
        bool HasAttributes() => current is not null
            && operation.Attributes.All(a => current.Attributes.TryGetValue(a.Key, out var value) && string.Equals(value, a.Value, StringComparison.Ordinal));

        return operation.Action switch
        {
            ActionCatalog.AccountCreate or ActionCatalog.EntitlementCreate or ActionCatalog.AccountUpdate => HasAttributes(),
            ActionCatalog.AccountEnable => current?.Enabled == true,
            ActionCatalog.AccountDisable => current?.Enabled == false,
            ActionCatalog.AccountDelete or ActionCatalog.EntitlementDelete => current is null,
            ActionCatalog.AccountWriteOwner => current?.Owner is { } owner && owner == operation.Related,
            ActionCatalog.EntitlementAdd or ActionCatalog.MemberAdd => Linked(operation.Related),
            ActionCatalog.EntitlementRemove or ActionCatalog.MemberRemove => current is not null && !Linked(operation.Related),
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

    private async Task<OperationResult> RunAsync(ActionDefinition definition, ConnectorOperation operation, CancellationToken cancellationToken)
    {
        var creates = operation.Action is ActionCatalog.AccountCreate or ActionCatalog.EntitlementCreate;
        var deletes = operation.Action is ActionCatalog.AccountDelete or ActionCatalog.EntitlementDelete;
        var current = await ReadCoreAsync(operation.Target, cancellationToken).ConfigureAwait(false);

        if (definition.Related is not null && operation.Related is not null
            && await ReadCoreAsync(operation.Related, cancellationToken).ConfigureAwait(false) is null)
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.RelatedNotFound);
        }

        if (current is null && !creates && !deletes)
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.TargetNotFound);
        }

        if (IsInDesiredState(operation, current))
        {
            return OperationResult.Of(operation, OperationStatus.AlreadyInState);
        }

        if (creates && current is not null)
        {
            // It exists, but not as asked: creating it again would overwrite something IAM didn't make.
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.AlreadyExistsDifferently);
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

        var shapeOk = operation.OperationId != Guid.Empty
            && operation.Target is { IsValid: true } && string.Equals(operation.Target.ObjectType, definition.Target, StringComparison.Ordinal) && IsValidId(operation.Target)
            && (definition.Related is null
                ? operation.Related is null
                : operation.Related is { IsValid: true } related && string.Equals(related.ObjectType, definition.Related, StringComparison.Ordinal) && IsValidId(related));
        if (!shapeOk)
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.InvalidOperation);
        }

        var attributes = operation.Attributes ?? new Dictionary<string, string?>();
        if (attributes.Keys.Any(ConnectorText.LooksLikeCredential))
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.CredentialInAttributes);
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

        if (!definition.NeedsSecretReference && operation.Secret is not null)
        {
            return OperationResult.Of(operation, OperationStatus.Rejected, ResultCodes.SecretReferenceNotAllowed);
        }

        return null;
    }
}
