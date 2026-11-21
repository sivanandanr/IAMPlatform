using IAM.BuildingBlocks.Abstractions.Messaging;

namespace IAM.Modules.Request.Contracts;

public static class RequestEvents
{
    public const string Source = "request";
}

/// <summary>
/// One stage of the route the matrix selected for an item, with every reference resolved to an id when the item was frozen
/// (DL-091): the source and exactly the fields it needs. Enum-like fields carry their stable names.
/// </summary>
public sealed record SubmittedStage(
    Guid StageId,
    int Sequence,
    string Kind,
    string Source,
    string? WorkgroupKey,
    Guid? ApplicationId,
    Guid? RoleTypeId,
    Guid? EntitlementId,
    string Quorum,
    int? MinimumApprovals,
    bool Mandatory,
    bool Independent,
    int DeadlineHours,
    string OnTimeout,
    IReadOnlyList<string> Fields);

/// <summary>
/// A request item was frozen and needs approval (DL-091): the people, what is asked, and the route the matrix selected with the
/// evidence of which version and rule chose it. The Workflow worker starts the approval from it, with the item id as the
/// instance id, so a message delivered twice starts it once.
/// </summary>
public sealed record RequestItemSubmitted(
    Guid ItemId,
    string Requester,
    string Beneficiary,
    Guid ApplicationId,
    Guid EntitlementId,
    Guid MatrixVersionId,
    string MatrixContentHash,
    Guid MatchedRuleId,
    string PolicyMaker,
    IReadOnlyList<SubmittedStage> Stages) : IIntegrationEvent
{
    public const int MaxStages = 10;

    public static string MessageType => "request.item.submitted";

    public static int SchemaVersion => 1;
}
