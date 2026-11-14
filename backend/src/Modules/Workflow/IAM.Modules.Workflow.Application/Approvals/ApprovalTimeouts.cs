namespace IAM.Modules.Workflow.Application.Approvals;

/// <summary>An open instance with a deadline that has passed, and the version to apply its timeouts against.</summary>
public sealed record DueApproval(Guid InstanceId, long Version);

/// <summary>Finds instances whose timeouts are due, for the timeout worker (DL-074).</summary>
public interface IDueApprovals
{
    /// <summary>
    /// Up to <paramref name="take"/> open instances whose next deadline is at or before <paramref name="now"/>, earliest
    /// first, leaving out <paramref name="skip"/> (instances the worker has set aside, such as damaged ones).
    /// </summary>
    Task<IReadOnlyList<DueApproval>> DueAsync(DateTimeOffset now, int take, IReadOnlySet<Guid> skip, CancellationToken cancellationToken);
}
