using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Notification.Infrastructure.Persistence;
using IAM.Modules.Workflow.Contracts;

namespace IAM.Modules.Notification.Infrastructure.Approvals;

/// <summary>
/// Turns the workflow's <c>task_opened</c> events into one pending notification per person (DL-078), at most once per
/// message. A notification grants nothing: the person still acts through the workflow, which checks everything again.
/// </summary>
public sealed class ApprovalTaskNotifications(NotificationDbContext db, IIamDatabaseProvider provider, TimeProvider clock) : IMessageHandler
{
    public const string Kind = "approval.task";

    /// <summary>The most people one event may name, as for an approval level (ADR-0027).</summary>
    public const int MaxPeople = 256;

    public string ConsumerName => "notification.approval_tasks";

    public string Source => WorkflowEvents.Source;

    public IReadOnlyList<string> MessageTypes { get; } = [ApprovalTaskOpened.MessageType];

    public async Task<MessageOutcome> HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var opened = MessageEnvelopes.Read<ApprovalTaskOpened>(envelope);
        if (opened.InstanceId == Guid.Empty || opened.RequestItemId == Guid.Empty || opened.StageId == Guid.Empty
            || opened.People is not { Count: > 0 and <= MaxPeople }
            || opened.People.Any(p => !PersonId.TryCreate(p, out _))
            || opened.People.Distinct(StringComparer.Ordinal).Count() != opened.People.Count)
        {
            throw new InvalidMessageException("A task_opened event must name 1 to 256 distinct valid people and its ids.");
        }

        return await db.ReceiveOnceAsync(
            provider,
            ConsumerName,
            envelope,
            clock.GetUtcNow(),
            () => db.Pending.AddRange(opened.People.Select(person => new PendingNotificationRow
            {
                Id = Guid.CreateVersion7(),
                Kind = Kind,
                Recipient = person,
                InstanceId = opened.InstanceId,
                RequestItemId = opened.RequestItemId,
                StageId = opened.StageId,
                DeadlineUtc = opened.DeadlineUtc,
                SourceMessageId = envelope.MessageId,
                CreatedForUtc = envelope.OccurredAtUtc,
            })),
            cancellationToken).ConfigureAwait(false);
    }
}
