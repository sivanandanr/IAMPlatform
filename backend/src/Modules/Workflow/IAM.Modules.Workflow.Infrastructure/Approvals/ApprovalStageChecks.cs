using IAM.Modules.Workflow.Application.Approvals;
using IAM.Modules.Workflow.Contracts;

namespace IAM.Modules.Workflow.Infrastructure.Approvals;

/// <summary>
/// Answers stage questions from the replayed instance (DL-092), the same way the workflow itself reads it: an instance that
/// fails its checks is not readable, so the answer is false. No permission check: the caller is another module that checks
/// its own, and the answer names no one.
/// </summary>
public sealed class ApprovalStageChecks(ApprovalWorkflowService workflow) : IApprovalStageChecks
{
    public async Task<bool> CanActOnOpenActionStageAsync(Guid instanceId, Guid stageId, string person, CancellationToken cancellationToken)
    {
        if (instanceId == Guid.Empty || stageId == Guid.Empty || string.IsNullOrEmpty(person))
        {
            return false;
        }

        ApprovalInstanceView? instance;
        try
        {
            instance = await workflow.FindAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApprovalConflictException or ApprovalInstanceDamagedException)
        {
            // Changing under us, or damaged: not a stage anyone can act on now (DL-097).
            return false;
        }

        // The workflow offers an Action task while the approval is in progress, or held once it is authorised (DL-097).
        var stage = instance?.Stages.FirstOrDefault(s => s.StageId == stageId);
        return (instance?.State == "InProgress" || instance is { State: "Held", ProvisioningAuthorized: true })
            && stage is { State: "Active", Kind: "Action" }
            && stage.Candidates.Contains(person, StringComparer.Ordinal);
    }
}
