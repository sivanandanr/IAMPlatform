using IAM.Modules.Request.Contracts;
using IAM.Modules.Request.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Request.Infrastructure.Items;

/// <summary>
/// Frozen request items for provisioning (DL-093), read with the request login. No permission check: the caller is another
/// module acting on a verified approval, and it checks what it may act on.
/// </summary>
public sealed class RequestItemSource(RequestDbContext db) : IRequestItemSource
{
    public async Task<ProvisionableItem?> FindAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var given = await db.AccountNames.AsNoTracking()
            .Where(n => n.ItemId == itemId)
            .OrderByDescending(n => n.SetAtUtc).ThenByDescending(n => n.Id)
            .Select(n => n.Name)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return new ProvisionableItem(
            item.Id,
            item.Requester,
            item.Beneficiary,
            item.ApplicationId,
            item.EntitlementId,
            item.EntitlementVersionId,
            item.EntitlementContentHash,
            item.AccountId,
            item.NewAccountType,
            given ?? item.NewAccountName);
    }
}
