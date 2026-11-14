using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Workflow.Application.Approvers;
using IAM.Modules.Workflow.Domain.Approvals;

namespace IAM.Modules.Workflow.Infrastructure.Approvers;

/// <summary>
/// Implements <see cref="IApprovalEscalation"/>: each person who proposes, approves, withdraws or escalates must hold
/// <c>workflow.approvals.escalate</c> now (DL-057a, DL-070c). A missing permission is refused before the engine is called,
/// so it changes nothing. Which roles hold the permission is data (ADR-0008); changing them needs a second person (DL-059).
/// </summary>
public sealed class ApprovalEscalationService(IPermissionEvaluator permissions, TimeProvider clock) : IApprovalEscalation
{
    public const string Action = AccessActions.ApprovalsEscalate;

    public async Task EscalateAsync(ApprovalInstance instance, Guid stageId, IReadOnlyList<PersonId> newCandidates, PersonId actor, string? reason, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        await RequirePermissionAsync(actor, cancellationToken).ConfigureAwait(false);
        instance.Escalate(stageId, newCandidates, actor, reason, expectedVersion, clock.GetUtcNow());
    }

    public async Task ProposeAsync(ApprovalInstance instance, Guid stageId, IReadOnlyList<PersonId> newCandidates, PersonId actor, string? reason, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        await RequirePermissionAsync(actor, cancellationToken).ConfigureAwait(false);
        instance.ProposeEscalation(stageId, newCandidates, actor, reason, expectedVersion, clock.GetUtcNow());
    }

    public async Task ApproveAsync(ApprovalInstance instance, Guid stageId, IReadOnlyList<PersonId> reviewedCandidates, PersonId approver, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        await RequirePermissionAsync(approver, cancellationToken).ConfigureAwait(false);
        instance.ApproveEscalation(stageId, reviewedCandidates, approver, expectedVersion, clock.GetUtcNow());
    }

    public async Task WithdrawAsync(ApprovalInstance instance, Guid stageId, PersonId actor, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        await RequirePermissionAsync(actor, cancellationToken).ConfigureAwait(false);
        instance.WithdrawEscalation(stageId, actor, expectedVersion, clock.GetUtcNow());
    }

    private async Task RequirePermissionAsync(PersonId person, CancellationToken cancellationToken)
    {
        if (!person.IsValid || !await permissions.CanAsync(person.Value, Action, cancellationToken).ConfigureAwait(false))
        {
            throw new ApprovalRefusedException(ApprovalRefusal.EscalatePermissionMissing);
        }
    }
}
