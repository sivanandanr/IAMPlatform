using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Provisioning.Infrastructure.Items;

/// <summary>The durable operation ledger in the provisioning schema (DL-093). A second record of the same operation is ignored.</summary>
public sealed class ProvisioningLedgerStore(ProvisioningDbContext db, IIamDatabaseProvider provider) : IProvisioningLedger
{
    public Task<OperationLedgerRecord?> FindAsync(Guid operationId, CancellationToken cancellationToken) =>
        db.Ledger.AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == operationId, cancellationToken);

    public async Task RecordAsync(OperationLedgerRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        db.Ledger.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (provider.IsUniqueViolation(ex))
        {
            // Another run recorded it first: the first record stands, as in the SDK's own ledger.
            db.ChangeTracker.Clear();
        }
    }
}
