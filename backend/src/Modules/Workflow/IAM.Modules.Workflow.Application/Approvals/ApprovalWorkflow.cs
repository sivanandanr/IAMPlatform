using IAM.BuildingBlocks.Abstractions.People;
using IAM.Modules.Workflow.Domain.Approvals;

namespace IAM.Modules.Workflow.Application.Approvals;

/// <summary>A stored approval instance as callers see it. People are sign-in ids; reasons and evidence stay in the audit trail.</summary>
public sealed record ApprovalInstanceView(
    Guid Id,
    Guid RequestItemId,
    string State,
    long Version,
    bool ProvisioningAuthorized,
    IReadOnlyList<ApprovalStageView> Stages);

public sealed record ApprovalStageView(
    Guid StageId,
    int Sequence,
    string State,
    string Kind,
    bool Mandatory,
    bool Independent,
    DateTimeOffset? DeadlineUtc,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> Approvers,
    bool EscalationPending);

/// <summary>No stored instance has this id.</summary>
public sealed class ApprovalInstanceNotFoundException : Exception
{
    public ApprovalInstanceNotFoundException()
        : base("Approval instance not found.")
    {
    }

    public ApprovalInstanceNotFoundException(string message)
        : base(message)
    {
    }

    public ApprovalInstanceNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Someone else changed the instance first. Reload it and try again; nothing was changed.</summary>
public sealed class ApprovalConflictException : Exception
{
    public ApprovalConflictException()
        : base("The approval instance changed; reload it.")
    {
    }

    public ApprovalConflictException(string message)
        : base(message)
    {
    }

    public ApprovalConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Replaying the stored actions did not reproduce the stored history or summary: the data was changed outside the
/// service, or an engine change alters old outcomes. Nothing can act on the instance until people look at it (DL-072a).
/// </summary>
public sealed class ApprovalInstanceDamagedException : Exception
{
    public ApprovalInstanceDamagedException()
        : base("The approval instance's stored record does not replay; it is refused.")
    {
    }

    public ApprovalInstanceDamagedException(string message)
        : base(message)
    {
    }

    public ApprovalInstanceDamagedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Durable approval workflows (ADR-0005, ADR-0028, DL-072). Every call loads the instance by replaying its stored actions,
/// checks the replay against the stored history, acts, resolves the approvers of any level that opened, and saves the new
/// actions, history and audit in one transaction. The acting person is the signed-in user, never a parameter. A refused
/// action changes nothing and is audited. Instances are started only by other modules through <see cref="StartAsync"/>
/// (DL-072b): there is no way for a person to start one with a route they chose.
/// </summary>
public interface IApprovalWorkflow
{
    /// <summary>
    /// Starts an instance on a validated route. Called by the module that owns the request (a later stage); the binding's
    /// instance id must be new. Its first levels open and get their approvers at once.
    /// </summary>
    Task<ApprovalInstanceView> StartAsync(ApprovalBinding binding, ApprovalRoute route, CancellationToken cancellationToken);

    /// <summary>The instance, or null. No permission check: for internal callers; the API applies its own reading rule (slice 2).</summary>
    Task<ApprovalInstanceView?> FindAsync(Guid instanceId, CancellationToken cancellationToken);

    Task<ApprovalInstanceView> DecideAsync(Guid instanceId, long expectedVersion, Guid decisionId, Guid stageId, PersonId? onBehalfOf, bool approve, string? reason, CancellationToken cancellationToken);

    Task<ApprovalInstanceView> CompleteAsync(Guid instanceId, long expectedVersion, Guid completionId, Guid stageId, PersonId? onBehalfOf, string? note, CancellationToken cancellationToken);

    Task<ApprovalInstanceView> ReturnForChangesAsync(Guid instanceId, long expectedVersion, Guid stageId, PersonId? onBehalfOf, string? reason, CancellationToken cancellationToken);

    /// <summary>The requester withdraws the request (the engine checks it is them).</summary>
    Task<ApprovalInstanceView> WithdrawAsync(Guid instanceId, long expectedVersion, string? reason, CancellationToken cancellationToken);

    /// <summary>Needs <c>workflow.instances.cancel</c> (DL-072e).</summary>
    Task<ApprovalInstanceView> CancelAsync(Guid instanceId, long expectedVersion, string? reason, CancellationToken cancellationToken);

    /// <summary>The escalation calls need <c>workflow.approvals.escalate</c> (DL-070c).</summary>
    Task<ApprovalInstanceView> EscalateAsync(Guid instanceId, long expectedVersion, Guid stageId, IReadOnlyList<PersonId> newCandidates, string? reason, CancellationToken cancellationToken);

    Task<ApprovalInstanceView> ProposeEscalationAsync(Guid instanceId, long expectedVersion, Guid stageId, IReadOnlyList<PersonId> newCandidates, string? reason, CancellationToken cancellationToken);

    Task<ApprovalInstanceView> ApproveEscalationAsync(Guid instanceId, long expectedVersion, Guid stageId, IReadOnlyList<PersonId> reviewedCandidates, string? reason, CancellationToken cancellationToken);

    Task<ApprovalInstanceView> WithdrawEscalationAsync(Guid instanceId, long expectedVersion, Guid stageId, string? reason, CancellationToken cancellationToken);

    /// <summary>Applies deadlines at the server's time. For the timeout worker (slice 3); nothing here can approve.</summary>
    Task<ApprovalInstanceView> ApplyTimeoutsAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken);
}
