using System.Diagnostics;
using System.Globalization;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Account.Contracts;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Catalog.Contracts;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Persistence;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Workflow.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IAM.Modules.Provisioning.Infrastructure.Items;

/// <summary>The reviewed allowlist of applications a connector serves (DL-093), from the worker's settings.</summary>
public sealed class ConnectorBindings(IEnumerable<ConnectorBinding> bindings)
{
    private readonly Dictionary<Guid, ConnectorBinding> _byApplication = bindings.ToDictionary(b => b.ApplicationId);

    public ConnectorBinding? For(Guid applicationId) => _byApplication.GetValueOrDefault(applicationId);
}

/// <summary>The audit codes of provisioning (DL-093).</summary>
public static class ProvisioningAuditActions
{
    public const string Target = "provisioning.item";
    public const string Planned = "provisioning.item.planned";
    public const string Blocked = "provisioning.item.blocked";
    public const string StepDone = "provisioning.item.step_done";
    public const string StepRetry = "provisioning.item.step_retry";
    public const string Failed = "provisioning.item.failed";
    public const string Succeeded = "provisioning.item.succeeded";
    public const string StepUndone = "provisioning.item.step_undone";
    public const string ReadAction = AccessActions.ProvisioningItemsRead;

    public static IReadOnlyList<string> All { get; } = [Planned, Blocked, StepDone, StepRetry, Failed, Succeeded, StepUndone];
}

/// <summary>
/// Acts on an approval's authorisation (DL-093, DL-096). What the item becomes is the <see cref="ProvisioningDecider"/>'s:
/// only a verified approval that authorises provisioning, for the item and route it was frozen with, is planned, and only
/// once no Action level is still open (a level that names the account comes after the decisions, DL-092). The <c>authorized</c>
/// event arrives when the decisions are done; if an Action level is still open, it waits for <c>closed</c>. Then it reads
/// the frozen item and checks again what may have changed since it was approved: the entitlement version, the existing
/// account, the new account's name and whether a connector serves the application. Anything that fails is blocked with a
/// reason, audited, and left for a person; nothing is granted. Planning runs nothing: the worker runs operations (slice 5b).
/// Either event, delivered any number of times, plans an item once.
/// </summary>
public sealed class AuthorizedApprovals(
    ProvisioningDbContext db,
    IIamDatabaseProvider provider,
    TimeProvider clock,
    IAuditWriter audit,
    ProvisioningDecider decider) : IMessageHandler
{
    public string ConsumerName => "provisioning.approvals";

    public string Source => WorkflowEvents.Source;

    public IReadOnlyList<string> MessageTypes => [ApprovalAuthorized.MessageType, ApprovalClosed.MessageType];

    public async Task<MessageOutcome> HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var (itemId, instanceId, sequence, closed) = envelope.Type == ApprovalClosed.MessageType
            ? Read(MessageEnvelopes.Read<ApprovalClosed>(envelope))
            : Read(MessageEnvelopes.Read<ApprovalAuthorized>(envelope));

        // The approval's id is the item's (DL-091); anything else isn't an authorisation this module can act on.
        if (itemId == Guid.Empty || instanceId != itemId || sequence < 1)
        {
            throw new InvalidMessageException("An authorisation must name its item, as its approval, and the action that authorised it.");
        }

        if (await db.Items.AsNoTracking().AnyAsync(i => i.Id == itemId, cancellationToken).ConfigureAwait(false))
        {
            return MessageOutcome.Duplicate;
        }

        var now = UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());
        var decision = await decider.DecideAsync(itemId, sequence, closed, now, cancellationToken).ConfigureAwait(false);
        if (decision is null)
        {
            // Nothing to plan yet (an Action level is open, or the event is early or forged) or ever (closed without
            // authorisation): the message is only noted, and blocks nothing (DL-096).
            return await db.ReceiveOnceAsync(provider, ConsumerName, envelope, now, () => { }, cancellationToken).ConfigureAwait(false);
        }

        // The request's correlation id, carried on to everything the runner does for this item (DL-094).
        decision.Item.Correlate(MessageCorrelation.IsTraceId(envelope.CorrelationId) ? envelope.CorrelationId : MessageCorrelation.Current());
        return await db.ReceiveOnceAsync(
            provider,
            ConsumerName,
            envelope,
            now,
            () =>
            {
                db.Items.Add(decision.Item);
                db.Operations.AddRange(decision.Operations);
            },
            cancellationToken,
            (tx, ct) => AuditAsync(tx, decision, now, ct)).ConfigureAwait(false);
    }

    /// <summary>The item, the approval, the least action count the message implies, and whether it is the closed event.</summary>
    private static (Guid Item, Guid Instance, int Sequence, bool Closed) Read(ApprovalAuthorized m) => (m.RequestItemId, m.InstanceId, m.ActionSequence, false);

    private static (Guid Item, Guid Instance, int Sequence, bool Closed) Read(ApprovalClosed m) => (m.RequestItemId, m.InstanceId, 1, true);

    private async Task AuditAsync(IDbContextTransaction tx, ProvisioningPlan decision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var item = decision.Item;
        var blocked = item.State == ProvisioningState.Blocked;
        await audit.AppendAsync(
            tx.GetDbTransaction(),
            new AuditEventRecord
            {
                Action = blocked ? ProvisioningAuditActions.Blocked : ProvisioningAuditActions.Planned,
                TargetType = ProvisioningAuditActions.Target,
                TargetId = item.Id.ToString(),
                Outcome = blocked ? AuditOutcome.Denied : AuditOutcome.Succeeded,
                Reason = blocked ? item.Problem : null,
                OccurredAtUtc = now,
                CorrelationId = item.CorrelationId ?? MessageCorrelation.Current(),
                Payload =
                [
                    Field("approvalActions", item.ApprovalActions?.ToString(CultureInfo.InvariantCulture) ?? "untrusted"),
                    Field("integrityProblem", item.IntegrityProblem ?? "none"),
                    Field("connector", item.ConnectorKey ?? "none"),
                    Field("operations", decision.Operations.Count.ToString(CultureInfo.InvariantCulture)),
                    Field("entitlementVersionId", item.EntitlementVersionId?.ToString() ?? "none"),
                    Field("accountId", item.AccountId?.ToString() ?? "none"),
                    Field("newAccountType", item.NewAccountType ?? "none"),
                    Field("accountName", item.AccountName ?? "none"),
                ],
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static AuditPayloadField Field(string name, string value) => new(name, value, AuditFieldClass.Plain);
}
