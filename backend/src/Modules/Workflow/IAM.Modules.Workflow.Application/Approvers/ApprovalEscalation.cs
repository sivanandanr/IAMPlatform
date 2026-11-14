using IAM.BuildingBlocks.Abstractions.People;
using IAM.Modules.Workflow.Domain.Approvals;

namespace IAM.Modules.Workflow.Application.Approvers;

/// <summary>
/// Choosing a held level's approvers, with the escalate permission checked server-side for every person who acts
/// (DL-048, DL-057a, DL-070c). The engine checks everything else. Time comes from the server clock, never the caller.
/// Nothing stores an instance yet: the workflow storage stage adds loading, saving and audit around these calls.
/// </summary>
public interface IApprovalEscalation
{
    /// <summary>One escalator, for a level that is neither mandatory nor independent.</summary>
    Task EscalateAsync(ApprovalInstance instance, Guid stageId, IReadOnlyList<PersonId> newCandidates, PersonId actor, string? reason, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>The first of two escalators, for a mandatory or independent level.</summary>
    Task ProposeAsync(ApprovalInstance instance, Guid stageId, IReadOnlyList<PersonId> newCandidates, PersonId actor, string? reason, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>The second escalator, naming the list they reviewed.</summary>
    Task ApproveAsync(ApprovalInstance instance, Guid stageId, IReadOnlyList<PersonId> reviewedCandidates, PersonId approver, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>Withdraws a waiting proposal.</summary>
    Task WithdrawAsync(ApprovalInstance instance, Guid stageId, PersonId actor, long expectedVersion, CancellationToken cancellationToken);
}
