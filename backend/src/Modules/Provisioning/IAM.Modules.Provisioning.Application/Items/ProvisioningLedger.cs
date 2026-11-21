using IAM.Modules.Provisioning.Domain.Items;

namespace IAM.Modules.Provisioning.Application.Items;

/// <summary>
/// The durable operation ledger (DL-093, R-039): the provisioning worker adapts it to the connector SDK's ledger, so a
/// connector that restarts still knows which operation ids it finished. The first record of an operation wins.
/// </summary>
public interface IProvisioningLedger
{
    Task<OperationLedgerRecord?> FindAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>Keeps the record unless the operation already has one, which stays as it is.</summary>
    Task RecordAsync(OperationLedgerRecord record, CancellationToken cancellationToken);
}
