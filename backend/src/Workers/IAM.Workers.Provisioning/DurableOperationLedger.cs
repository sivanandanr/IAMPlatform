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
    public async Task<LedgerEntry?> FindAsync(Guid operationId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<IProvisioningLedger>().FindAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        // A stored status the SDK doesn't know is damage: refuse it rather than guess what happened. So is a failure: the SDK never
        // records one (DL-105), and a replayed failure would say a write that may have landed was never made.
        if (!Enum.TryParse<OperationStatus>(record.Status, ignoreCase: false, out var status) || !Enum.IsDefined(status) || status.ToString() != record.Status
            || status == OperationStatus.Failed)
        {
            throw new InvalidDataException("A ledger record has a status the SDK never records.");
        }

        return new LedgerEntry(record.OperationId, record.Fingerprint, new OperationResult(record.OperationId, status, record.Code));
    }

    public async Task RecordAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Result.OperationId != entry.OperationId)
        {
            throw new ArgumentException("A ledger entry's result must be for its own operation.", nameof(entry));
        }

        var record = OperationLedgerRecord.Create(entry.OperationId, entry.Fingerprint, entry.Result.Status.ToString(), entry.Result.Code, clock.GetUtcNow());
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProvisioningLedger>().RecordAsync(record, cancellationToken).ConfigureAwait(false);
    }
}
