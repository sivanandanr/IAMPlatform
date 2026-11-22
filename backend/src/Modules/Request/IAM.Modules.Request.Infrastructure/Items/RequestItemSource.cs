using System.Text.Json;
using IAM.BuildingBlocks.Messaging;
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
    public async Task<ProvisionableItem?> FindAsync(Guid itemId, ApprovedAccountName? approvedName, CancellationToken cancellationToken)
    {
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        // Exactly the assignment the approval recorded (DL-099), and only this item's; else the naming rule's proposal.
        var name = item.NewAccountName;
        if (approvedName?.AssignmentId is { } assignmentId)
        {
            name = await db.AccountNames.AsNoTracking()
                .Where(n => n.Id == assignmentId && n.ItemId == itemId)
                .Select(n => n.Name)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

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
            name,
            item.CorrelationId,
            await db.AccountNames.AsNoTracking().AnyAsync(n => n.ItemId == itemId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<AccountNameSnapshot?> CurrentAccountNameAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.Items.AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => new { i.NewAccountType, i.NewAccountName })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (item?.NewAccountType is null)
        {
            return null;
        }

        var latest = await db.AccountNames.AsNoTracking()
            .Where(n => n.ItemId == itemId)
            .OrderByDescending(n => n.Seq)
            .Select(n => new { n.Id, n.Name })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return latest is not null ? new AccountNameSnapshot(latest.Id, latest.Name)
            : item.NewAccountName is { } proposal ? new AccountNameSnapshot(null, proposal)
            : null;
    }

    public async Task<RequestItemSubmitted?> FindSubmittedAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var stages = JsonSerializer.Deserialize<List<SubmittedStage>>(item.Route, MessageEnvelopes.Json)
            ?? throw new InvalidDataException("A frozen item's route doesn't read.");
        return new RequestItemSubmitted(
            item.Id, item.Requester, item.Beneficiary, item.ApplicationId, item.EntitlementId, item.MatrixVersionId, item.MatrixContentHash,
            item.MatchedRuleId, item.PolicyMaker, stages);
    }
}
