using IAM.Modules.Workflow.Application.Approvals;
using IAM.Modules.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Workflow.Infrastructure.Approvals;

/// <summary>Implements <see cref="IDueApprovals"/> on the summary rows' state and next deadline (indexed).</summary>
public sealed class DueApprovals(WorkflowDbContext db) : IDueApprovals
{
    public const int MaxTake = 500;

    private static readonly string[] OpenStates = ["InProgress", "Held"];

    public async Task<IReadOnlyList<DueApproval>> DueAsync(DateTimeOffset now, int take, IReadOnlySet<Guid> skip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, MaxTake);

        // Skipped ids are few (damaged instances, until restart), so they are left out here rather than in SQL.
        var rows = await db.Instances.AsNoTracking()
            .Where(i => OpenStates.Contains(i.State) && i.NextDeadlineUtc != null && i.NextDeadlineUtc <= now)
            .OrderBy(i => i.NextDeadlineUtc)
            .ThenBy(i => i.Id)
            .Take(take + Math.Min(skip.Count, MaxTake))
            .Select(i => new DueApproval(i.Id, i.EngineVersion))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Where(r => !skip.Contains(r.InstanceId)).Take(take)];
    }
}
