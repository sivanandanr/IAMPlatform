using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Messaging;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Workflow.Application.Approvals;
using IAM.Modules.Workflow.Domain.Approvals;

namespace IAM.Modules.Workflow.Infrastructure.Approvals;

/// <summary>
/// Starts the approval of a submitted request item (DL-091), from the route the matrix selected when the item was frozen. The
/// instance id is the item id, so a message delivered twice starts one approval: the second finds it and is a duplicate.
/// A route or binding the workflow refuses is a message it can never process (dead-lettered, and its refusal audited).
/// </summary>
public sealed class RequestItemApprovals(IApprovalWorkflow workflow) : IMessageHandler
{
    public string ConsumerName => "workflow.request_items";

    public string Source => RequestEvents.Source;

    public IReadOnlyList<string> MessageTypes => [RequestItemSubmitted.MessageType];

    public async Task<MessageOutcome> HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var item = MessageEnvelopes.Read<RequestItemSubmitted>(envelope);
        var (binding, route) = Build(item);
        try
        {
            await workflow.StartAsync(binding, route, cancellationToken).ConfigureAwait(false);
            return MessageOutcome.Processed;
        }
        catch (ApprovalConflictException)
        {
            return MessageOutcome.Duplicate;
        }
        catch (ApprovalRefusedException ex)
        {
            throw new InvalidMessageException("The workflow refused the item's route or binding.", ex);
        }
    }

    /// <summary>The binding and route the item names, or <see cref="InvalidMessageException"/> for anything malformed.</summary>
    public static (ApprovalBinding Binding, ApprovalRoute Route) Build(RequestItemSubmitted item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ItemId == Guid.Empty || item.Stages is not { Count: > 0 and <= RequestItemSubmitted.MaxStages }
            || !PersonId.TryCreate(item.Requester, out var requester) || !PersonId.TryCreate(item.Beneficiary, out var beneficiary)
            || !PersonId.TryCreate(item.PolicyMaker, out var policyMaker))
        {
            throw new InvalidMessageException("A submitted item must name its people and 1 to 10 stages.");
        }

        var binding = new ApprovalBinding(
            item.ItemId, item.ItemId, item.MatrixVersionId, item.MatrixContentHash ?? string.Empty, item.MatchedRuleId, item.ItemId, requester, beneficiary, policyMaker);
        try
        {
            return (binding, ApprovalRoute.Create([.. item.Stages.Select(Stage)]));
        }
        catch (ApprovalRefusedException ex)
        {
            throw new InvalidMessageException("The workflow refused the item's route.", ex);
        }
    }

    private static StageDefinition Stage(SubmittedStage s)
    {
        ApproverSource source = s.Source switch
        {
            "EntitlementOwner" when s.EntitlementId is { } id && s.ApplicationId is null && s.RoleTypeId is null && s.WorkgroupKey is null => ApproverSource.EntitlementOwner(id),
            "ApplicationOwner" when s.ApplicationId is { } app && s.RoleTypeId is null && s.EntitlementId is null && s.WorkgroupKey is null => ApproverSource.ApplicationOwner(app),
            "ApplicationRole" when s.ApplicationId is { } app && s.RoleTypeId is { } role && s.EntitlementId is null && s.WorkgroupKey is null => ApproverSource.ApplicationRole(app, role),
            "Workgroup" when s.WorkgroupKey is { } key && s.ApplicationId is null && s.RoleTypeId is null && s.EntitlementId is null => ApproverSource.Workgroup(key),
            _ => throw new InvalidMessageException("A stage names an unknown source or the wrong fields for it."),
        };
        return new StageDefinition(
            s.StageId,
            s.Sequence,
            Parse<QuorumMode>(s.Quorum),
            s.MinimumApprovals,
            s.Mandatory,
            s.Independent,
            Applicable: true,
            s.DeadlineHours is >= 1 and <= 2160 ? TimeSpan.FromHours(s.DeadlineHours) : throw new InvalidMessageException("A stage deadline is out of range."),
            Parse<TimeoutAction>(s.OnTimeout),
            source,
            Parse<StageKind>(s.Kind));
    }

    private static TEnum Parse<TEnum>(string? text)
        where TEnum : struct, Enum =>
        text is not null && Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal)
            ? Enum.Parse<TEnum>(text)
            : throw new InvalidMessageException("A stage names an unknown value.");
}
