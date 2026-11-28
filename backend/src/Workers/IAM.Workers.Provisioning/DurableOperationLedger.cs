using IAM.Connectors.Sdk;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Workers.Provisioning;

/// <summary>
/// The connector SDK's operation ledger, kept in the provisioning schema (DL-093, R-039), so a connector that restarts still
/// knows which operation ids it finished and a repeat changes nothing twice. Each call uses its own scope: the SDK may call it
/// for several operations at once.
/// </summary>
public sealed class DurableOperationLedger(IServiceScopeFactory scopes, TimeProvider clock) : IOperationLedger
{
    /// <summary>The status a stored record the SDK doesn't know is handed over as: no member of <see cref="OperationStatus"/>.</summary>
    public const OperationStatus UnknownStatus = 0;

    public async Task<LedgerEntry?> FindAsync(Guid operationId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<IProvisioningLedger>().FindAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        // A stored status the SDK doesn't know is handed over as a status no member has, never guessed and never "not there"
        // (which would run the operation again): the SDK's replay check refuses it as damage, as it refuses a stored failure
        // (DL-105, DL-107). Only the SDK judges a record (DL-109).
        var status = Enum.TryParse<OperationStatus>(record.Status, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == record.Status
            ? parsed
            : UnknownStatus;
        return new LedgerEntry(record.OperationId, record.Fingerprint, new OperationResult(record.OperationId, status, record.Code));
    }

    public async Task RecordAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Result.OperationId != entry.OperationId)
        {
            throw new ArgumentException("A ledger entry's result must be for its own operation.", nameof(entry));
        }

        // A failure is never remembered (DL-105): the first record wins, so one would poison the operation id (DL-106). The
        // database refuses it too.
        if (entry.Result.Status == OperationStatus.Failed)
        {
            throw new ArgumentException("A failed operation is never recorded.", nameof(entry));
        }

        var record = OperationLedgerRecord.Create(entry.OperationId, entry.Fingerprint, entry.Result.Status.ToString(), entry.Result.Code, clock.GetUtcNow());
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProvisioningLedger>().RecordAsync(record, cancellationToken).ConfigureAwait(false);
    }
}
