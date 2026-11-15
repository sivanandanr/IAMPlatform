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

        // Skipped ids are left out in SQL, so however many are set aside a pass still gets a full batch (DL-075g).
        var skipped = skip.ToArray();
        return await db.Instances.AsNoTracking()
            .Where(i => OpenStates.Contains(i.State) && i.NextDeadlineUtc != null && i.NextDeadlineUtc <= now && !skipped.Contains(i.Id))
            .OrderBy(i => i.NextDeadlineUtc)
            .ThenBy(i => i.Id)
            .Take(take)
            .Select(i => new DueApproval(i.Id, i.EngineVersion))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
