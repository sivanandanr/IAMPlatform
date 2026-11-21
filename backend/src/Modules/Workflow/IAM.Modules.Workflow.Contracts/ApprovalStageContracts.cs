namespace IAM.Modules.Workflow.Contracts;

/// <summary>
/// What other modules may ask about an approval's stages (ADR-0001, DL-092). Read-only; it never changes an approval.
/// </summary>
public interface IApprovalStageChecks
{
    /// <summary>
    /// Whether <paramref name="person"/> may act now on the stage: the approval exists, the stage is an Action stage that is open
    /// (active), and the person is one of its resolved candidates. Anything unknown is false.
    /// </summary>
    Task<bool> CanActOnOpenActionStageAsync(Guid instanceId, Guid stageId, string person, CancellationToken cancellationToken);
}
