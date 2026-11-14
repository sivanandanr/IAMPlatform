using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Workflow.Application.Approvals;
using IAM.Modules.Workflow.Application.Approvers;
using IAM.Modules.Workflow.Domain.Approvals;
using IAM.Modules.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IAM.Modules.Workflow.Infrastructure.Approvals;

/// <summary>Audit action codes of approval workflows (registered with the audit writer by the Workflow module).</summary>
public static class WorkflowAuditActions
{
    public const string TargetType = "workflow.approval";
    public const string Refused = "workflow.approval.refused";
    public const string Damaged = "workflow.approval.damaged";

    public const string EscalateAction = AccessActions.ApprovalsEscalate;
    public const string CancelAction = AccessActions.InstancesCancel;

    /// <summary>One code per action kind, for example <c>workflow.approval.decided</c>.</summary>
    public static string For(ApprovalActionKind kind) => kind switch
    {
        ApprovalActionKind.Bound => "workflow.approval.started",
        ApprovalActionKind.ApproversSupplied => "workflow.approval.approvers_supplied",
        ApprovalActionKind.Decided => "workflow.approval.decided",
        ApprovalActionKind.Completed => "workflow.approval.completed",
        ApprovalActionKind.Returned => "workflow.approval.returned",
        ApprovalActionKind.Withdrawn => "workflow.approval.withdrawn",
        ApprovalActionKind.Cancelled => "workflow.approval.cancelled",
        ApprovalActionKind.Escalated => "workflow.approval.escalated",
        ApprovalActionKind.EscalationProposed => "workflow.approval.escalation_proposed",
        ApprovalActionKind.EscalationApproved => "workflow.approval.escalation_approved",
        ApprovalActionKind.EscalationWithdrawn => "workflow.approval.escalation_withdrawn",
        ApprovalActionKind.TimeoutsApplied => "workflow.approval.timeouts_applied",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static IReadOnlyList<string> All { get; } =
        [.. Enum.GetValues<ApprovalActionKind>().Select(For), Refused, Damaged];
}

/// <summary>
/// Implements <see cref="IApprovalWorkflow"/> (DL-072). Each call runs in one transaction: load the instance by replaying
/// its action log and check the replay against the stored history and summary; act through the same log path the replay
/// uses; resolve and supply the approvers of every level that opened; save the new actions, history and summary; audit.
/// A refusal changes nothing and is audited in its own transaction. Writes are serialised on the summary row's version.
/// </summary>
public sealed class ApprovalWorkflowService(
    WorkflowDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IIamDatabaseProvider databaseProvider,
    IAuditActorAccessor actorAccessor,
    IPermissionEvaluator permissions,
    IApproverResolver resolver) : IApprovalWorkflow
{
    public async Task<ApprovalInstanceView> StartAsync(ApprovalBinding binding, ApprovalRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(route);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = Now();
        LoggedAction bound;
        ApprovalInstance instance;
        try
        {
            bound = ApprovalActionLog.Bound(binding, route, now);
            instance = ApprovalActionLog.Start(bound);
        }
        catch (ApprovalRefusedException refusal)
        {
            await RefuseAsync(tx, binding.InstanceId == Guid.Empty ? null : binding.InstanceId, "start", refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (await db.Instances.AnyAsync(i => i.Id == binding.InstanceId, cancellationToken).ConfigureAwait(false))
        {
            throw new ApprovalConflictException("An approval instance with this id already exists.");
        }

        var loaded = new Loaded(
            new ApprovalInstanceRow
            {
                Id = binding.InstanceId,
                RequestItemId = binding.RequestItemId,
                Requester = binding.Requester.Value,
                Beneficiary = binding.Beneficiary.Value,
                State = instance.State.ToString(),
                EngineVersion = 0,
                ActionCount = 0,
                ProvisioningAuthorized = false,
            },
            instance,
            route.Stages,
            StoredHistory: 0);
        db.Instances.Add(loaded.Row);
        List<(LoggedAction Action, long VersionAfter)> actions = [(bound, instance.Version)];
        await SupplyOpenedLevelsAsync(loaded, actions, now, cancellationToken).ConfigureAwait(false);
        await SaveAsync(tx, loaded, actions, cancellationToken).ConfigureAwait(false);
        return View(loaded);
    }

    public async Task<ApprovalInstanceView?> FindAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var row = await db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : View(await ReplayAsync(row, null, cancellationToken).ConfigureAwait(false));
    }

    public Task<ApprovalInstanceView> DecideAsync(Guid instanceId, long expectedVersion, Guid decisionId, Guid stageId, PersonId? onBehalfOf, bool approve, string? reason, CancellationToken cancellationToken) =>
        ActAsync(instanceId, "decide", null, (actor, now) =>
            ApprovalActionLog.Decided(new ApprovalDecision(decisionId, stageId, actor, onBehalfOf, approve, reason, expectedVersion), now), cancellationToken);

    public Task<ApprovalInstanceView> CompleteAsync(Guid instanceId, long expectedVersion, Guid completionId, Guid stageId, PersonId? onBehalfOf, string? note, CancellationToken cancellationToken) =>
        ActAsync(instanceId, "complete", null, (actor, now) =>
            ApprovalActionLog.Completed(new ActionCompletion(completionId, stageId, actor, onBehalfOf, note, expectedVersion), now), cancellationToken);

    public Task<ApprovalInstanceView> ReturnForChangesAsync(Guid instanceId, long expectedVersion, Guid stageId, PersonId? onBehalfOf, string? reason, CancellationToken cancellationToken) =>
        ActAsync(instanceId, "return", null, (actor, now) => ApprovalActionLog.Returned(stageId, actor, onBehalfOf, reason, expectedVersion, now), cancellationToken);

    public Task<ApprovalInstanceView> WithdrawAsync(Guid instanceId, long expectedVersion, string? reason, CancellationToken cancellationToken) =>
        ActAsync(instanceId, "withdraw", null, (actor, now) => ApprovalActionLog.Withdrawn(actor, reason, expectedVersion, now), cancellationToken);

    public Task<ApprovalInstanceView> CancelAsync(Guid instanceId, long expectedVersion, string? reason, CancellationToken cancellationToken) =>
        ActAsync(instanceId, "cancel", WorkflowAuditActions.CancelAction, (actor, now) => ApprovalActionLog.Cancelled(actor, reason, expectedVersion, now), cancellationToken);

    public Task<ApprovalInstanceView> EscalateAsync(Guid instanceId, long expectedVersion, Guid stageId, IReadOnlyList<PersonId> newCandidates, string? reason, CancellationToken cancellationToken) =>
        ActAsync(instanceId, "escalate", WorkflowAuditActions.EscalateAction, (actor, now) => ApprovalActionLog.Escalated(stageId, newCandidates, actor, reason, expectedVersion, now), cancellationToken);

    public Task<ApprovalInstanceView> ProposeEscalationAsync(Guid instanceId, long expectedVersion, Guid stageId, IReadOnlyList<PersonId> newCandidates, string? reason, CancellationToken cancellationToken) =>
        ActAsync(instanceId, "propose_escalation", WorkflowAuditActions.EscalateAction, (actor, now) => ApprovalActionLog.Proposed(stageId, newCandidates, actor, reason, expectedVersion, now), cancellationToken);

    public Task<ApprovalInstanceView> ApproveEscalationAsync(Guid instanceId, long expectedVersion, Guid stageId, IReadOnlyList<PersonId> reviewedCandidates, string? reason, CancellationToken cancellationToken) =>
        ActAsync(instanceId, "approve_escalation", WorkflowAuditActions.EscalateAction, (actor, now) => ApprovalActionLog.EscalationApproved(stageId, reviewedCandidates, actor, reason, expectedVersion, now), cancellationToken);

    public Task<ApprovalInstanceView> WithdrawEscalationAsync(Guid instanceId, long expectedVersion, Guid stageId, string? reason, CancellationToken cancellationToken) =>
        ActAsync(instanceId, "withdraw_escalation", WorkflowAuditActions.EscalateAction, (actor, now) => ApprovalActionLog.EscalationWithdrawn(stageId, actor, reason, expectedVersion, now), cancellationToken);

    public Task<ApprovalInstanceView> ApplyTimeoutsAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken) =>
        ActAsync(instanceId, "timeouts", null, (_, now) => ApprovalActionLog.TimeoutsApplied(expectedVersion, now), cancellationToken, needsPerson: false);

    private async Task<ApprovalInstanceView> ActAsync(
        Guid instanceId,
        string step,
        string? permission,
        Func<PersonId, DateTimeOffset, LoggedAction> build,
        CancellationToken cancellationToken,
        bool needsPerson = true)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Instances.SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new ApprovalInstanceNotFoundException();
        var loaded = await ReplayAsync(row, tx, cancellationToken).ConfigureAwait(false);
        var now = Now();
        LoggedAction action;
        try
        {
            var actor = default(PersonId);
            if (needsPerson && !PersonId.TryCreate(actorAccessor.CurrentActor, out actor))
            {
                throw new ApprovalRefusedException(ApprovalRefusal.InvalidActor);
            }

            if (permission is not null && !await permissions.CanAsync(actor.Value, permission, cancellationToken).ConfigureAwait(false))
            {
                throw new ApprovalRefusedException(permission == WorkflowAuditActions.CancelAction ? ApprovalRefusal.CancelPermissionMissing : ApprovalRefusal.EscalatePermissionMissing);
            }

            action = build(actor, now);
            ApprovalActionLog.Apply(loaded.Instance, action);
        }
        catch (ApprovalRefusedException refusal)
        {
            await RefuseAsync(tx, instanceId, step, refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }

        List<(LoggedAction Action, long VersionAfter)> actions = [(action, loaded.Instance.Version)];
        await SupplyOpenedLevelsAsync(loaded, actions, now, cancellationToken).ConfigureAwait(false);
        await SaveAsync(tx, loaded, actions, cancellationToken).ConfigureAwait(false);
        return View(loaded);
    }

    /// <summary>
    /// Gives every level that opened its approvers, resolved now from its source (DL-069), each as a logged action so a
    /// replay needs no resolver. A supplied level can complete at once (a carried approval) and open the next step, so
    /// this repeats; each level is supplied once, so it ends.
    /// </summary>
    private async Task SupplyOpenedLevelsAsync(Loaded loaded, List<(LoggedAction Action, long VersionAfter)> actions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var instance = loaded.Instance;
        HashSet<PersonId> excluded = [instance.Binding.Requester, instance.Binding.Beneficiary, instance.Binding.PolicyMaker];
        while (instance.State is ApprovalState.InProgress or ApprovalState.Held
            && instance.Stages.FirstOrDefault(s => s.State == StageState.AwaitingApprovers && now < s.DeadlineUtc) is { } waiting)
        {
            var resolution = await resolver.ResolveAsync(waiting.Source, excluded, cancellationToken).ConfigureAwait(false);
            var supplied = ApprovalActionLog.Supplied(waiting.StageId, resolution, now);
            ApprovalActionLog.Apply(instance, supplied);
            actions.Add((supplied, instance.Version));
        }
    }

    /// <summary>
    /// Rebuilds the instance from its log and checks it against what was stored: every history event, and the summary.
    /// Anything that doesn't match is damage, audited and refused (DL-072a).
    /// </summary>
    private async Task<Loaded> ReplayAsync(ApprovalInstanceRow row, IDbContextTransaction? open, CancellationToken cancellationToken)
    {
        var actions = await db.Actions.AsNoTracking().Where(a => a.InstanceId == row.Id).OrderBy(a => a.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        var history = await db.History.AsNoTracking().Where(h => h.InstanceId == row.Id).OrderBy(h => h.EventVersion).ToListAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (actions.Count == 0 || actions.Count != row.ActionCount || actions.Select((a, i) => a.Sequence != i + 1).Any(gap => gap))
            {
                throw new InvalidDataException("The action log is incomplete.");
            }

            var logged = actions.Select(a => new LoggedAction(Enum.Parse<ApprovalActionKind>(a.Kind), a.Payload, a.AtUtc, a.Actor)).ToList();
            var instance = ApprovalActionLog.Start(logged[0]);
            if (instance.Version != actions[0].VersionAfter)
            {
                throw new InvalidDataException("The action log does not replay.");
            }

            for (var i = 1; i < logged.Count; i++)
            {
                ApprovalActionLog.Apply(instance, logged[i]);
                if (instance.Version != actions[i].VersionAfter)
                {
                    throw new InvalidDataException("The action log does not replay.");
                }
            }

            if (!SameHistory(instance.History, history)
                || row.State != instance.State.ToString()
                || row.EngineVersion != instance.Version
                || row.ProvisioningAuthorized != instance.ProvisioningAuthorized
                || row.Id != instance.Binding.InstanceId
                || row.RequestItemId != instance.Binding.RequestItemId
                || row.Requester != instance.Binding.Requester.Value
                || row.Beneficiary != instance.Binding.Beneficiary.Value)
            {
                throw new InvalidDataException("The stored history or summary does not match the replay.");
            }

            return new Loaded(row, instance, ApprovalActionLog.StagesOf(logged[0]), history.Count);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ApprovalRefusedException or ArgumentException or FormatException)
        {
            // Audited in the caller's transaction when there is one, else in its own; either way nothing else is saved.
            db.ChangeTracker.Clear();
            var tx = open ?? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await audit.AppendAsync(tx.GetDbTransaction(), Event(WorkflowAuditActions.Damaged, row.Id, AuditOutcome.Denied, null, Field("step", "load")), cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (open is null)
                {
                    await tx.DisposeAsync().ConfigureAwait(false);
                }
            }

            throw new ApprovalInstanceDamagedException("The approval instance's stored record does not replay; it is refused.", ex);
        }
    }

    private static bool SameHistory(IReadOnlyList<ApprovalEvent> replayed, List<ApprovalHistoryRow> stored) =>
        replayed.Count == stored.Count
        && replayed.Zip(stored).All(pair =>
            pair.First.Version == pair.Second.EventVersion
            && pair.First.AtUtc == pair.Second.AtUtc
            && pair.First.Kind.ToString() == pair.Second.Kind
            && pair.First.StageId == pair.Second.StageId
            && (pair.First.Actor is { } actor ? actor.Value : null) == pair.Second.Actor);

    private async Task SaveAsync(IDbContextTransaction tx, Loaded loaded, List<(LoggedAction Action, long VersionAfter)> actions, CancellationToken cancellationToken)
    {
        var instance = loaded.Instance;
        var row = loaded.Row;
        var sequence = row.ActionCount;
        foreach (var (action, versionAfter) in actions)
        {
            db.Actions.Add(new ApprovalActionRow
            {
                InstanceId = row.Id,
                Sequence = ++sequence,
                Kind = action.Kind.ToString(),
                Payload = action.Payload,
                AtUtc = action.AtUtc,
                Actor = action.Actor,
                VersionAfter = versionAfter,
            });
        }

        foreach (var e in instance.History.Skip(loaded.StoredHistory))
        {
            db.History.Add(new ApprovalHistoryRow
            {
                InstanceId = row.Id,
                EventVersion = e.Version,
                AtUtc = e.AtUtc,
                Kind = e.Kind.ToString(),
                StageId = e.StageId,
                Actor = e.Actor is { } actor ? actor.Value : null,
            });
        }

        row.State = instance.State.ToString();
        row.EngineVersion = instance.Version;
        row.ActionCount = sequence;
        row.ProvisioningAuthorized = instance.ProvisioningAuthorized;
        row.NextDeadlineUtc = NextDeadline(loaded);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ApprovalConflictException("The approval instance changed; reload it.", ex);
        }
        catch (DbUpdateException ex) when (databaseProvider.IsUniqueViolation(ex))
        {
            throw new ApprovalConflictException("The approval instance changed; reload it.", ex);
        }

        foreach (var (action, versionAfter) in actions)
        {
            await audit.AppendAsync(tx.GetDbTransaction(), ActionRecord(row, action, versionAfter), cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The earliest deadline that a timeout could still act on: an open level's, or a held level that will reject or cancel.</summary>
    private static DateTimeOffset? NextDeadline(Loaded loaded)
    {
        var onTimeout = loaded.Stages.ToDictionary(s => s.StageId, s => s.OnTimeout);
        return loaded.Instance.Stages
            .Where(s => s.State is StageState.Active or StageState.AwaitingApprovers
                || (s.State == StageState.Held && onTimeout[s.StageId] != TimeoutAction.Escalate))
            .Select(s => s.DeadlineUtc)
            .Min();
    }

    private async Task RefuseAsync(IDbContextTransaction tx, Guid? instanceId, string step, ApprovalRefusedException refusal, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await audit.AppendAsync(
            tx.GetDbTransaction(),
            Event(WorkflowAuditActions.Refused, instanceId, AuditOutcome.Denied, null, Field("step", step), Field("refusal", (refusal.Refusal ?? ApprovalRefusal.InvalidDecision).ToString())),
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private AuditEventRecord ActionRecord(ApprovalInstanceRow row, LoggedAction action, long versionAfter)
    {
        List<AuditPayloadField> payload =
        [
            Field("kind", action.Kind.ToString()),
            Field("versionAfter", versionAfter.ToString(CultureInfo.InvariantCulture)),
            Field("state", row.State),
            Field("provisioningAuthorized", row.ProvisioningAuthorized ? "true" : "false"),
        ];

        // The action's inputs are the evidence: the binding and route, a decision, a resolution with its steps.
        payload.Add(Field("inputs", action.Payload));
        return Event(WorkflowAuditActions.For(action.Kind), row.Id, AuditOutcome.Succeeded, null, [.. payload]);
    }

    private static ApprovalInstanceView View(Loaded loaded)
    {
        var instance = loaded.Instance;
        return new ApprovalInstanceView(
            instance.Binding.InstanceId,
            instance.Binding.RequestItemId,
            instance.State.ToString(),
            instance.Version,
            instance.ProvisioningAuthorized,
            [.. instance.Stages.Select(s => new ApprovalStageView(
                s.StageId,
                s.Sequence,
                s.State.ToString(),
                s.Kind.ToString(),
                s.Mandatory,
                s.Independent,
                s.DeadlineUtc,
                [.. s.Candidates.Select(c => c.Value)],
                [.. s.Approvers.Select(a => a.Value)],
                s.PendingEscalation is not null))]);
    }

    private DateTimeOffset Now() => UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());

    private AuditEventRecord Event(string action, Guid? targetId, AuditOutcome outcome, string? reason, params AuditPayloadField[] payload) => new()
    {
        Action = action,
        TargetType = WorkflowAuditActions.TargetType,
        TargetId = targetId?.ToString(),
        Outcome = outcome,
        OccurredAtUtc = Now(),
        CorrelationId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N"),
        Reason = reason,
        Payload = payload,
    };

    private static AuditPayloadField Field(string name, string value) => new(name, value, AuditFieldClass.Plain);

    /// <summary>A loaded instance: its summary row, the replayed engine, its route, and how much history was stored.</summary>
    private sealed record Loaded(ApprovalInstanceRow Row, ApprovalInstance Instance, IReadOnlyList<StageDefinition> Stages, int StoredHistory);
}
