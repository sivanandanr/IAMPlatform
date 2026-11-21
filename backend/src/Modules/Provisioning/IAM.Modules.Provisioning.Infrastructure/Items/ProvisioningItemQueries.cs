using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Provisioning.Infrastructure.Items;

/// <summary>
/// Blocked and failed items for the IAM team (DL-094), read only. The permission is checked here as well as at the endpoint,
/// so no other caller reaches the rows without it. Steps carry codes and object ids only, never a connector's message.
/// </summary>
public sealed class ProvisioningItemQueries(ProvisioningDbContext db, IAuditActorAccessor actor, IPermissionEvaluator permissions) : IProvisioningItems
{
    private static readonly HashSet<string> Listable = new(StringComparer.Ordinal) { nameof(ProvisioningState.Blocked), nameof(ProvisioningState.Failed) };

    public async Task<ProvisioningItemPage> ListAsync(string state, int page, int pageSize, CancellationToken cancellationToken)
    {
        await RequireAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || !Listable.Contains(state) || page is < 1 or > IProvisioningItems.MaxPage || pageSize is < 1 or > IProvisioningItems.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "State must be Blocked or Failed, with a bounded page.");
        }

        var wanted = Enum.Parse<ProvisioningState>(state);
        var items = await db.Items.AsNoTracking()
            .Where(i => i.State == wanted)
            .OrderByDescending(i => i.ReceivedAtUtc).ThenByDescending(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var ids = items.Select(i => i.Id).ToList();
        var steps = (await db.Operations.AsNoTracking().Where(o => ids.Contains(o.ItemId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(o => o.ItemId);
        return new ProvisioningItemPage([.. items.Select(i => View(i, steps[i.Id]))], page, pageSize);
    }

    public async Task<ProvisioningItemView?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await RequireAsync(cancellationToken).ConfigureAwait(false);
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var steps = await db.Operations.AsNoTracking().Where(o => o.ItemId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return View(item, steps);
    }

    private async Task RequireAsync(CancellationToken cancellationToken)
    {
        if (actor.CurrentActor is not { Length: > 0 } subject
            || !await permissions.CanAsync(subject, ProvisioningAuditActions.ReadAction, cancellationToken).ConfigureAwait(false))
        {
            throw new ProvisioningAccessDeniedException();
        }
    }

    private static ProvisioningItemView View(ProvisioningItem i, IEnumerable<ProvisioningOperation> steps) => new(
        i.Id,
        i.State.ToString(),
        i.Problem,
        i.IntegrityProblem,
        i.Beneficiary,
        i.ApplicationId,
        i.EntitlementId,
        i.AccountId,
        i.NewAccountType,
        i.AccountName,
        i.ConnectorKey,
        i.Attempts,
        i.RecordedAccountId,
        i.ReceivedAtUtc,
        i.FinishedAtUtc,
        [.. steps.OrderBy(s => s.Step).Select(s => new ProvisioningStepView(s.Step, s.Action, s.TargetId, s.RelatedId, s.State.ToString(), s.ResultCode, s.CompletedAtUtc))]);
}
