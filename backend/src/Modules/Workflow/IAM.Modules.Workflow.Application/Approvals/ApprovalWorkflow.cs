using IAM.BuildingBlocks.Abstractions.People;
using IAM.Modules.Workflow.Domain.Approvals;

namespace IAM.Modules.Workflow.Application.Approvals;

/// <summary>
/// A stored approval instance as a participant sees it (DL-073a): each level, who can act and who decided, with reasons
/// only for rejections and returns. <see cref="Oversight"/> is set only for holders of <c>workflow.instances.read</c>.
/// </summary>
public sealed record ApprovalInstanceView(
    Guid Id,
    Guid RequestItemId,
    string State,
    long Version,
    bool ProvisioningAuthorized,
    IReadOnlyList<ApprovalStageView> Stages,
    ApprovalClosureView? Closure,
    ApprovalOversightView? Oversight = null);

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
    bool EscalationPending,
    IReadOnlyList<ApprovalDecisionView> Decisions);

/// <summary>One recorded decision or task. <see cref="Reason"/> is shown to participants only for a rejection.</summary>
public sealed record ApprovalDecisionView(string Person, string? Delegate, string Outcome, string? Reason, DateTimeOffset AtUtc);

/// <summary>How the request closed, if a person closed it. The reason is shown to participants only for a return.</summary>
public sealed record ApprovalClosureView(string Outcome, string Actor, string? Reason, DateTimeOffset AtUtc);

/// <summary>What only <c>workflow.instances.read</c> holders see: the full history and evidence (DL-073a).</summary>
public sealed record ApprovalOversightView(
    IReadOnlyList<ApprovalHistoryEntryView> History,
    IReadOnlyList<ApprovalEscalationView> Escalations,
    IReadOnlyList<ApprovalEscalationWithdrawalView> EscalationWithdrawals,
    IReadOnlyList<ApprovalResolutionView> Resolutions,
    IReadOnlyList<ApprovalDecisionView> AllDecisions,
    string? ClosureReason);

public sealed record ApprovalHistoryEntryView(long Version, DateTimeOffset AtUtc, string Kind, Guid? StageId, string? Actor);

public sealed record ApprovalEscalationView(
    Guid StageId,
    string Actor,
    string Reason,
    string? SecondActor,
    string? SecondReason,
    IReadOnlyList<string> PreviousCandidates,
    IReadOnlyList<string> NewCandidates,
    DateTimeOffset AtUtc);

public sealed record ApprovalEscalationWithdrawalView(Guid StageId, string Actor, string Reason, DateTimeOffset AtUtc);

public sealed record ApprovalResolutionView(Guid StageId, string Source, string Outcome, IReadOnlyList<string> People, IReadOnlyList<ApprovalResolutionStepView> Steps);

public sealed record ApprovalResolutionStepView(string Source, string Result, string? Reference, int? Version, string? Hash, int Found, int Excluded);

/// <summary>One open level the signed-in person can act on now (DL-073b).</summary>
public sealed record ApprovalTaskView(Guid InstanceId, Guid RequestItemId, Guid StageId, int Sequence, string Kind, DateTimeOffset? DeadlineUtc, long Version);

public sealed record ApprovalTaskPage(IReadOnlyList<ApprovalTaskView> Items, int Page, int PageSize, bool HasMore);

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
/// action changes nothing and is audited. Someone who neither takes part in an instance nor holds a permission that
/// reaches it gets 'not found' (DL-073c). Instances are started only by other modules through <see cref="StartAsync"/>
/// (DL-072b): there is no way for a person to start one with a route they chose.
/// </summary>
public interface IApprovalWorkflow
{
    /// <summary>
    /// Starts an instance on a validated route. Called by the module that owns the request (a later stage); the binding's
    /// instance id must be new. Its first levels open and get their approvers at once.
    /// </summary>
    Task<ApprovalInstanceView> StartAsync(ApprovalBinding binding, ApprovalRoute route, CancellationToken cancellationToken);

    /// <summary>
    /// The instance as the signed-in person may see it, or null (DL-072d, DL-073a): participants (the requester, the person
    /// getting access, anyone ever listed on a level or who acted) see the participant view; holders of
    /// <c>workflow.instances.read</c> also see the oversight. Anyone else gets null, as for an id that doesn't exist. A
    /// damaged instance is null too, unless the caller holds <c>workflow.instances.read</c> (DL-075b).
    /// </summary>
    Task<ApprovalInstanceView?> ReadAsync(Guid instanceId, CancellationToken cancellationToken);

    /// <summary>The open levels the signed-in person can act on now, earliest deadline first (DL-073b). Page is 1-based.</summary>
    Task<ApprovalTaskPage> MyTasksAsync(int page, int pageSize, CancellationToken cancellationToken);

    // Nobody acts on another's behalf until a delegation registry exists (DL-075d), so these take no principal.
    Task<ApprovalInstanceView> DecideAsync(Guid instanceId, long expectedVersion, Guid decisionId, Guid stageId, bool approve, string? reason, CancellationToken cancellationToken);

    Task<ApprovalInstanceView> CompleteAsync(Guid instanceId, long expectedVersion, Guid completionId, Guid stageId, string? note, CancellationToken cancellationToken);

    Task<ApprovalInstanceView> ReturnForChangesAsync(Guid instanceId, long expectedVersion, Guid stageId, string? reason, CancellationToken cancellationToken);

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
