using IAM.BuildingBlocks.Abstractions.Messaging;

namespace IAM.Modules.Workflow.Contracts;

/// <summary>
/// What the Workflow module publishes through its outbox (DL-076). Ids, codes, versions and sign-in ids only: never a
/// reason, note, route or resolution evidence. Each event is written in the same transaction as the action that caused
/// it, so it exists only if that action was saved. <c>EngineVersion</c> is the instance's history version after it.
/// Delivery is at least once and in no promised order, even for events of one action: deduplicate on the message id and
/// order an instance's events by <c>EngineVersion</c>.
/// </summary>
public static class WorkflowEvents
{
    public const string Source = "workflow";
}

/// <summary>An approval instance was started for a request item.</summary>
public sealed record ApprovalStarted(Guid InstanceId, Guid RequestItemId, string Requester, string Beneficiary, long EngineVersion) : IIntegrationEvent
{
    public static string MessageType => "workflow.approval.started";

    public static int SchemaVersion => 1;
}

/// <summary>
/// People can now act on a level: it opened, or new approvers were added to it. <see cref="People"/> are only the people
/// who weren't able to act on it before this action. A notification, never a permission: acting still goes through the
/// workflow, which checks everything again.
/// </summary>
public sealed record ApprovalTaskOpened(
    Guid InstanceId,
    Guid RequestItemId,
    Guid StageId,
    int Sequence,
    string Kind,
    IReadOnlyList<string> People,
    DateTimeOffset? DeadlineUtc,
    long EngineVersion) : IIntegrationEvent
{
    public static string MessageType => "workflow.approval.task_opened";

    public static int SchemaVersion => 1;
}

/// <summary>
/// Every applicable decision level was approved: provisioning may start once the action levels after it are done.
/// <para>
/// <b>A consumer that acts on this must first verify the instance against the audit chain</b> (R-037 release gate,
/// DL-075, DL-076a): the workflow's database login could write this row by SQL. Until that check exists, nothing may
/// provision from it.
/// </para>
/// </summary>
public sealed record ApprovalAuthorized(Guid InstanceId, Guid RequestItemId, int ActionSequence, long EngineVersion) : IIntegrationEvent
{
    public static string MessageType => "workflow.approval.authorized";

    public static int SchemaVersion => 1;
}

/// <summary>The instance closed: Approved, Rejected, Cancelled, Withdrawn or Returned. Nothing changes after this.</summary>
public sealed record ApprovalClosed(Guid InstanceId, Guid RequestItemId, string Outcome, bool ProvisioningAuthorized, long EngineVersion) : IIntegrationEvent
{
    public static string MessageType => "workflow.approval.closed";

    public static int SchemaVersion => 1;
}
