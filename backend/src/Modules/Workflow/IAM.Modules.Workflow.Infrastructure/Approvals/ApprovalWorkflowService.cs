using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IAM.BuildingBlocks.Abstractions.Messaging;
using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Workflow.Application.Approvals;
using IAM.Modules.Workflow.Application.Approvers;
using IAM.Modules.Workflow.Contracts;
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
    public const string ReadAction = AccessActions.InstancesRead;

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
    IApproverResolver resolver,
    IRequestItemSource items) : IApprovalWorkflow
{
    /// <summary>Bounds on the inbox: at most 100 a page and 1,000 pages, so a request can't ask the database for everything.</summary>
    public const int MaxTaskPageSize = 100;

    /// <summary>
    /// The correlation id of each instance this scope started or loaded (DL-094), by instance id (DL-097): one scope may act on
    /// several instances, and each audits with its own.
    /// </summary>
    private readonly Dictionary<Guid, string?> _correlations = [];

    public const int MaxTaskPage = 1_000;

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
                CorrelationId = _correlations[binding.InstanceId] = MessageCorrelation.Current(),
            },
            instance,
            route.Stages,
            StoredHistory: 0);
        db.Instances.Add(loaded.Row);
        List<(LoggedAction Action, long VersionAfter)> actions = [(bound, instance.Version)];
        await SupplyOpenedLevelsAsync(tx, loaded, actions, now, "start", cancellationToken).ConfigureAwait(false);
        await SaveAsync(tx, loaded, actions, Before.Nothing, cancellationToken).ConfigureAwait(false);
        return View(loaded, oversight: false);
    }

    /// <summary>
    /// The instance with its oversight, or null, with no permission check. Not on <see cref="IApprovalWorkflow"/>, so no
    /// other module or endpoint can reach it (DL-075d): this module's own code and tests only.
    /// </summary>
    public async Task<ApprovalInstanceView?> FindAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var row = await db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : View(await ReplayAsync(row, null, cancellationToken).ConfigureAwait(false), oversight: true);
    }

    public async Task<ApprovalInstanceView?> ReadAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (!PersonId.TryCreate(actorAccessor.CurrentActor, out var actor))
        {
            return null;
        }

        var canRead = await CanAsync(actor, WorkflowAuditActions.ReadAction, cancellationToken).ConfigureAwait(false);
        Loaded? loaded = null;
        for (var attempt = 1; loaded is null; attempt++)
        {
            var row = await db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            try
            {
                loaded = await ReplayAsync(row, null, cancellationToken).ConfigureAwait(false);
            }
            catch (ApprovalConflictException) when (attempt < 3)
            {
                // Someone saved while it was being read (DL-075i): read it again.
            }
            catch (ApprovalInstanceDamagedException) when (!canRead)
            {
                // Its stored people can't be trusted to say who participates, so only oversight learns it exists (DL-075b).
                return null;
            }
        }

        return canRead || IsParticipant(loaded.Instance, actor) ? View(loaded, oversight: canRead) : null;
    }

    public async Task<ApprovalTaskPage> MyTasksAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, MaxTaskPage);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxTaskPageSize);
        if (!PersonId.TryCreate(actorAccessor.CurrentActor, out var actor))
        {
            return new ApprovalTaskPage([], page, pageSize, HasMore: false);
        }

        var person = actor.Value;
        var rows = await (
                from task in db.Tasks.AsNoTracking()
                join instance in db.Instances.AsNoTracking() on task.InstanceId equals instance.Id
                where task.Person == person
                orderby task.DeadlineUtc == null, task.DeadlineUtc, task.InstanceId, task.StageId
                select new ApprovalTaskView(task.InstanceId, task.RequestItemId, task.StageId, task.Sequence, task.Kind, task.DeadlineUtc, instance.EngineVersion))
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new ApprovalTaskPage([.. rows.Take(pageSize)], page, pageSize, rows.Count > pageSize);
    }

    public Task<ApprovalInstanceView> DecideAsync(Guid instanceId, long expectedVersion, Guid decisionId, Guid stageId, bool approve, string? reason, CancellationToken cancellationToken) =>
        ActAsync(instanceId, "decide", null, (actor, now) =>
            ApprovalActionLog.Decided(new ApprovalDecision(decisionId, stageId, actor, null, approve, reason, expectedVersion), now), cancellationToken);

    /// <summary>
    /// Completes an Action level. For a request item that asks for a new account, the account name as it stands now is recorded
    /// with the completion (DL-099): provisioning uses exactly that name. The approval's instance id is the item id (DL-091). A
    /// name given while this call runs is not the one recorded; it never counts, since the level is closed by this completion.
    /// </summary>
    public async Task<ApprovalInstanceView> CompleteAsync(Guid instanceId, long expectedVersion, Guid completionId, Guid stageId, string? note, CancellationToken cancellationToken, string? expectedAccountName = null)
    {
        var name = await items.CurrentAccountNameAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return await ActAsync(instanceId, "complete", null, (actor, now) =>
            expectedAccountName is not null && !string.Equals(name?.Name, expectedAccountName, StringComparison.Ordinal)
                ? throw new ApprovalRefusedException(ApprovalRefusal.AccountNameChanged)
                : ApprovalActionLog.Completed(
                new ActionCompletion(completionId, stageId, actor, null, note, expectedVersion, name?.AssignmentId, name is null ? null : AccountNameHash.Of(name.Name)),
                now), cancellationToken).ConfigureAwait(false);
    }

    public Task<ApprovalInstanceView> ReturnForChangesAsync(Guid instanceId, long expectedVersion, Guid stageId, string? reason, CancellationToken cancellationToken) =>
        ActAsync(instanceId, "return", null, (actor, now) => ApprovalActionLog.Returned(stageId, actor, null, reason, expectedVersion, now), cancellationToken);

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
        var actor = default(PersonId);
        var validActor = needsPerson && PersonId.TryCreate(actorAccessor.CurrentActor, out actor);
        Loaded loaded;
        try
        {
            loaded = await ReplayAsync(row, tx, cancellationToken).ConfigureAwait(false);
        }
        catch (ApprovalInstanceDamagedException) when (needsPerson)
        {
            // Only oversight learns that a damaged instance exists (DL-075b); the damage itself is audited once.
            if (validActor && await CanAsync(actor, WorkflowAuditActions.ReadAction, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            throw new ApprovalInstanceNotFoundException();
        }

        var before = Before.Of(loaded);
        var now = Now();
        LoggedAction action;
        try
        {
            if (needsPerson && !validActor)
            {
                throw new ApprovalRefusedException(ApprovalRefusal.InvalidActor);
            }

            // Someone the instance doesn't concern, with no permission that reaches it, can't tell it exists (DL-073c).
            if (needsPerson
                && !IsParticipant(loaded.Instance, actor)
                && !await CanAsync(actor, WorkflowAuditActions.ReadAction, cancellationToken).ConfigureAwait(false)
                && !(permission is not null && await CanAsync(actor, permission, cancellationToken).ConfigureAwait(false)))
            {
                await RefuseAsync(tx, instanceId, step, "NotVisible", cancellationToken).ConfigureAwait(false);
                throw new ApprovalInstanceNotFoundException();
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

        // An action that changed nothing (timeouts with nothing expired) isn't stored: the log holds only what happened.
        if (loaded.Instance.Version == row.EngineVersion)
        {
            return View(loaded, oversight: false);
        }

        List<(LoggedAction Action, long VersionAfter)> actions = [(action, loaded.Instance.Version)];
        await SupplyOpenedLevelsAsync(tx, loaded, actions, now, step, cancellationToken).ConfigureAwait(false);
        await SaveAsync(tx, loaded, actions, before, cancellationToken).ConfigureAwait(false);
        return View(loaded, oversight: needsPerson && await CanAsync(actor, WorkflowAuditActions.ReadAction, cancellationToken).ConfigureAwait(false));
    }

    private async Task<bool> CanAsync(PersonId person, string action, CancellationToken cancellationToken) =>
        person.IsValid && await permissions.CanAsync(person.Value, action, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The requester, the person getting access, anyone ever listed on a level (configured, resolved, escalated or proposed)
    /// and anyone who acted on it (DL-072d).
    /// </summary>
    private static bool IsParticipant(ApprovalInstance instance, PersonId person) =>
        person == instance.Binding.Requester
        || person == instance.Binding.Beneficiary
        || instance.History.Any(e => e.Actor == person)
        || instance.Stages.Any(s => s.Candidates.Contains(person) || s.Source.People.Contains(person)
            || (s.Resolution?.People.Contains(person) ?? false) || (s.PendingEscalation?.Candidates.Contains(person) ?? false))
        || instance.Escalations.Any(e => e.PreviousCandidates.Contains(person) || e.NewCandidates.Contains(person));

    /// <summary>
    /// Gives every level that opened its approvers, resolved now from its source (DL-069), each as a logged action so a
    /// replay needs no resolver. A supplied level can complete at once (a carried approval) and open the next step, so
    /// this repeats; each level is supplied once, so it ends. A refusal here is audited like any other and nothing is saved
    /// (DL-075e).
    /// </summary>
    private async Task SupplyOpenedLevelsAsync(IDbContextTransaction tx, Loaded loaded, List<(LoggedAction Action, long VersionAfter)> actions, DateTimeOffset now, string step, CancellationToken cancellationToken)
    {
        var instance = loaded.Instance;
        HashSet<PersonId> excluded = [instance.Binding.Requester, instance.Binding.Beneficiary, instance.Binding.PolicyMaker];
        try
        {
            while (instance.State is ApprovalState.InProgress or ApprovalState.Held
                && instance.Stages.FirstOrDefault(s => s.State == StageState.AwaitingApprovers && now < s.DeadlineUtc) is { } waiting)
            {
                var resolution = await resolver.ResolveAsync(waiting.Source, excluded, cancellationToken).ConfigureAwait(false);
                var supplied = ApprovalActionLog.Supplied(waiting.StageId, resolution, now);
                ApprovalActionLog.Apply(instance, supplied);
                actions.Add((supplied, instance.Version));
            }
        }
        catch (ApprovalRefusedException refusal)
        {
            await RefuseAsync(tx, instance.Binding.InstanceId, step, refusal, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Rebuilds the instance from its log and checks it against what was stored: every history event, and the summary.
    /// Anything that doesn't match is damage, audited and refused (DL-072a).
    /// </summary>
    private async Task<Loaded> ReplayAsync(ApprovalInstanceRow row, IDbContextTransaction? open, CancellationToken cancellationToken)
    {
        _correlations[row.Id] = row.CorrelationId;
        var actions = await db.Actions.AsNoTracking().Where(a => a.InstanceId == row.Id).OrderBy(a => a.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        var history = await db.History.AsNoTracking().Where(h => h.InstanceId == row.Id).OrderBy(h => h.EventVersion).ToListAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Rebuild(row, actions, history);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The summary, log and history are read by separate statements, so a writer committing in between makes them
            // disagree. If the summary has moved since it was read, that is what happened: a conflict, not damage (DL-075i).
            var now = await db.Instances.AsNoTracking()
                .Where(i => i.Id == row.Id)
                .Select(i => new { i.ActionCount, i.EngineVersion })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (now is not null && (now.ActionCount != row.ActionCount || now.EngineVersion != row.EngineVersion))
            {
                throw new ApprovalConflictException("The approval instance changed while it was loaded; reload it.", ex);
            }

            // Anything else that stops the replay is damage (DL-075c), audited once per instance (DL-075b).
            db.ChangeTracker.Clear();
            await RecordDamageAsync(row.Id, open, cancellationToken).ConfigureAwait(false);
            throw new ApprovalInstanceDamagedException("The approval instance's stored record does not replay; it is refused.", ex);
        }
    }

    /// <summary>
    /// Rebuilds an instance from its stored action log and checks the log, history and summary agree (DL-072). Throws
    /// <see cref="InvalidDataException"/> (or the engine's own refusal) for anything that doesn't replay. Reads nothing and
    /// records nothing: the caller decides whether a failure is damage or a conflict. Also used by the integrity check (DL-093).
    /// </summary>
    internal static Loaded Rebuild(ApprovalInstanceRow row, List<ApprovalActionRow> actions, List<ApprovalHistoryRow> history)
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

        var loaded = new Loaded(row, instance, ApprovalActionLog.StagesOf(logged[0]), history.Count);
        if (row.NextDeadlineUtc != NextDeadline(loaded))
        {
            throw new InvalidDataException("The stored next deadline does not match the replay.");
        }

        return loaded;
    }

    /// <summary>
    /// Records damage the first time it is found: a row in <c>ApprovalDamage</c> and one audit event, in the caller's
    /// transaction when there is one, else in its own. Later loads find the row and write nothing.
    /// </summary>
    private async Task RecordDamageAsync(Guid instanceId, IDbContextTransaction? open, CancellationToken cancellationToken)
    {
        var tx = open ?? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await db.Damage.AnyAsync(d => d.InstanceId == instanceId, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            db.Damage.Add(new ApprovalDamageRow { InstanceId = instanceId, DetectedAtUtc = Now() });
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (databaseProvider.IsUniqueViolation(ex))
            {
                // Someone else recorded it first, and audited it there.
                return;
            }

            await audit.AppendAsync(tx.GetDbTransaction(), Event(WorkflowAuditActions.Damaged, instanceId, AuditOutcome.Denied, null, Field("step", "load")), cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
            if (open is null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
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

    private async Task SaveAsync(IDbContextTransaction tx, Loaded loaded, List<(LoggedAction Action, long VersionAfter)> actions, Before before, CancellationToken cancellationToken)
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

        // The inbox is rebuilt for this instance in the same transaction (DL-073b).
        await db.Tasks.Where(x => x.InstanceId == row.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var tasks = TasksOf(loaded).ToList();
        db.Tasks.AddRange(tasks);

        // What other modules hear about, in the same transaction: it exists only if this is saved (DL-076).
        db.Outbox.AddRange(EventsOf(loaded, before, tasks, sequence, actions[^1].Action.AtUtc).Select(OutboxMessageRow.From));
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

        var first = row.ActionCount - actions.Count;
        for (var i = 0; i < actions.Count; i++)
        {
            var (action, versionAfter) = actions[i];
            await audit.AppendAsync(tx.GetDbTransaction(), ActionRecord(row, first + i + 1, action, versionAfter), cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The earliest deadline for which applying timeouts would still change something, by the engine's own rule (DL-075f):
    /// an open level's, a held level that will reject or cancel, or a held level whose timeout isn't recorded yet.
    /// </summary>
    private static DateTimeOffset? NextDeadline(Loaded loaded)
    {
        var onTimeout = loaded.Stages.ToDictionary(s => s.StageId, s => s.OnTimeout);
        return loaded.Instance.Stages
            .Where(s => s.State is StageState.Active or StageState.AwaitingApprovers
                || (s.State == StageState.Held && (onTimeout[s.StageId] != TimeoutAction.Escalate || !s.TimeoutRecorded)))
            .Select(s => s.DeadlineUtc)
            .Min();
    }

    private Task RefuseAsync(IDbContextTransaction tx, Guid? instanceId, string step, ApprovalRefusedException refusal, CancellationToken cancellationToken) =>
        RefuseAsync(tx, instanceId, step, (refusal.Refusal ?? ApprovalRefusal.InvalidDecision).ToString(), cancellationToken);

    private async Task RefuseAsync(IDbContextTransaction tx, Guid? instanceId, string step, string code, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await audit.AppendAsync(
            tx.GetDbTransaction(),
            Event(WorkflowAuditActions.Refused, instanceId, AuditOutcome.Denied, null, Field("step", step), Field("refusal", code)),
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Who can act on which open level now: eligible people on an active level who haven't had their say, while the request
    /// accepts decisions, or tasks once it is authorised (the engine's own rule). Only a lookup for the inbox.
    /// </summary>
    private static IEnumerable<ApprovalTaskRow> TasksOf(Loaded loaded)
    {
        var instance = loaded.Instance;
        var open = instance.State == ApprovalState.InProgress;
        var tasksOnly = instance.State == ApprovalState.Held && instance.ProvisioningAuthorized;
        if (!open && !tasksOnly)
        {
            yield break;
        }

        foreach (var stage in instance.Stages.Where(s => s.State == StageState.Active && (open || s.Kind == StageKind.Action)))
        {
            var decided = instance.Decisions.Where(d => d.StageId == stage.StageId).SelectMany(d => new[] { d.Actor, d.Principal }).ToHashSet();
            foreach (var person in stage.Eligible.Where(p => !decided.Contains(p)).Distinct())
            {
                yield return new ApprovalTaskRow
                {
                    InstanceId = instance.Binding.InstanceId,
                    StageId = stage.StageId,
                    Person = person.Value,
                    RequestItemId = instance.Binding.RequestItemId,
                    Sequence = stage.Sequence,
                    Kind = stage.Kind.ToString(),
                    DeadlineUtc = stage.DeadlineUtc,
                };
            }
        }
    }

    /// <summary>
    /// The audit record of one stored action: what it was and a hash of its row, keyed by sequence (DL-075a). The inputs stay
    /// in the append-only log, so no route, reason or list of people has to fit the audit's size or content rules.
    /// </summary>
    private AuditEventRecord ActionRecord(ApprovalInstanceRow row, int sequence, LoggedAction action, long versionAfter)
    {
        List<AuditPayloadField> fields =
        [
            Field("kind", action.Kind.ToString()),
            Field("sequence", sequence.ToString(CultureInfo.InvariantCulture)),
            Field("versionAfter", versionAfter.ToString(CultureInfo.InvariantCulture)),
            Field("state", row.State),
            Field("provisioningAuthorized", row.ProvisioningAuthorized ? "true" : "false"),
            Field("actionHash", ApprovalActionLog.RowHash(row.Id, sequence, action, versionAfter)),
        ];

        // The account name a completed level stood behind (DL-099): its assignment and hash, never the name itself.
        if (ApprovalActionLog.CompletionOf(action) is { NameHash: { } nameHash } completion)
        {
            fields.Add(Field("nameAssignmentId", completion.NameAssignmentId?.ToString("D") ?? "proposal"));
            fields.Add(Field("nameHash", nameHash));
        }

        return Event(WorkflowAuditActions.For(action.Kind), row.Id, AuditOutcome.Succeeded, null, [.. fields]);
    }

    /// <summary>
    /// The participant view, and with <paramref name="oversight"/> the full history and evidence (DL-073a). Participants
    /// see a decision's reason only for a rejection, and a closure's reason only for a return.
    /// </summary>
    private static ApprovalInstanceView View(Loaded loaded, bool oversight)
    {
        var instance = loaded.Instance;
        var closure = instance.Closure is { } c
            ? new ApprovalClosureView(c.Outcome.ToString(), c.Actor.Value, c.Outcome == ApprovalState.Returned ? c.Reason : null, c.AtUtc)
            : null;
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
                [.. s.Candidates.Select(p => p.Value)],
                [.. s.Approvers.Select(p => p.Value)],
                s.PendingEscalation is not null,
                [.. instance.Decisions.Where(d => d.StageId == s.StageId).Select(d => Decision(d, withReason: d.Outcome == RecordedOutcome.Rejected))]))],
            closure,
            oversight ? Oversight(instance) : null);
    }

    private static ApprovalDecisionView Decision(RecordedDecision d, bool withReason) =>
        new(d.Principal.Value, d.Actor == d.Principal ? null : d.Actor.Value, d.Outcome.ToString(), withReason ? d.Reason : null, d.AtUtc);

    private static ApprovalOversightView Oversight(ApprovalInstance instance) => new(
        [.. instance.History.Select(e => new ApprovalHistoryEntryView(e.Version, e.AtUtc, e.Kind.ToString(), e.StageId, e.Actor is { } a ? a.Value : null))],
        [.. instance.Escalations.Select(e => new ApprovalEscalationView(
            e.StageId,
            e.Actor.Value,
            e.Reason,
            e.SecondActor is { } second ? second.Value : null,
            e.SecondReason,
            [.. e.PreviousCandidates.Select(p => p.Value)],
            [.. e.NewCandidates.Select(p => p.Value)],
            e.AtUtc))],
        [.. instance.EscalationWithdrawals.Select(w => new ApprovalEscalationWithdrawalView(w.StageId, w.Actor.Value, w.Reason, w.AtUtc))],
        [.. instance.Stages.Where(s => s.Resolution is not null).Select(s => new ApprovalResolutionView(
            s.StageId,
            s.Source.Kind.ToString(),
            s.Resolution!.Outcome.ToString(),
            [.. s.Resolution.People.Select(p => p.Value)],
            [.. s.Resolution.Steps.Select(x => new ApprovalResolutionStepView(x.Source.ToString(), x.Result.ToString(), x.Reference, x.Version, x.Hash, x.Found, x.Excluded))]))],
        [.. instance.Decisions.Select(d => Decision(d, withReason: true))],
        instance.Closure?.Reason);

    private DateTimeOffset Now() => UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());

    private AuditEventRecord Event(string action, Guid? targetId, AuditOutcome outcome, string? reason, params AuditPayloadField[] payload) => new()
    {
        Action = action,
        TargetType = WorkflowAuditActions.TargetType,
        TargetId = targetId?.ToString(),
        Outcome = outcome,
        OccurredAtUtc = Now(),

        // The instance's own correlation (the request's), with this call's trace as the cause when they differ (DL-094).
        CorrelationId = Stored(targetId) ?? MessageCorrelation.Current(),
        CausationId = Stored(targetId) is { } stored && stored != MessageCorrelation.Current() ? MessageCorrelation.Current() : null,
        Reason = reason,
        Payload = payload,
    };

    private static AuditPayloadField Field(string name, string value) => new(name, value, AuditFieldClass.Plain);

    /// <summary>The stored correlation id of an instance this scope started or loaded, else null.</summary>
    private string? Stored(Guid? instanceId) => instanceId is { } id ? _correlations.GetValueOrDefault(id) : null;

    /// <summary>
    /// The events an action caused (DL-076): started, people newly able to act on a level, authorised, closed. Each has an
    /// idempotency key fixed by the instance, the last action's sequence and the event, so the same save can't give two.
    /// </summary>
    private static IEnumerable<MessageEnvelope> EventsOf(Loaded loaded, Before before, List<ApprovalTaskRow> tasks, int lastSequence, DateTimeOffset at)
    {
        var instance = loaded.Instance;
        var id = instance.Binding.InstanceId;
        var item = instance.Binding.RequestItemId;
        var correlation = loaded.Row.CorrelationId ?? MessageCorrelation.Current();
        var causation = string.Create(CultureInfo.InvariantCulture, $"workflow.action:{id:N}:{lastSequence}");

        MessageEnvelope Envelope<T>(T message, string? key = null)
            where T : IIntegrationEvent =>
            MessageEnvelopes.Create(message, WorkflowEvents.Source, correlation, causation, $"{causation}:{T.MessageType}{key}", at);

        if (before.State is null)
        {
            yield return Envelope(new ApprovalStarted(id, item, instance.Binding.Requester.Value, instance.Binding.Beneficiary.Value, instance.Version));
        }

        foreach (var opened in tasks.Where(t => !before.Tasks.Contains((t.StageId, t.Person))).GroupBy(t => t.StageId).OrderBy(g => g.First().Sequence))
        {
            // At most MaxPeople per event, so a level of 256 long ids can't outgrow the payload limit and fail the action.
            var first = opened.First();
            var chunks = opened.Select(t => t.Person).Order(StringComparer.Ordinal).Chunk(ApprovalTaskOpened.MaxPeople).ToList();
            for (var part = 0; part < chunks.Count; part++)
            {
                yield return Envelope(
                    new ApprovalTaskOpened(id, item, first.StageId, first.Sequence, first.Kind, chunks[part], first.DeadlineUtc, instance.Version),
                    string.Create(CultureInfo.InvariantCulture, $":{first.StageId:N}:{part}"));
            }
        }

        if (!before.Authorized && instance.ProvisioningAuthorized)
        {
            yield return Envelope(new ApprovalAuthorized(id, item, lastSequence, instance.Version));
        }

        if (IsClosed(instance.State) && !(before.State is { } was && IsClosed(was)))
        {
            yield return Envelope(new ApprovalClosed(id, item, instance.State.ToString(), instance.ProvisioningAuthorized, instance.Version));
        }
    }

    private static bool IsClosed(ApprovalState state) =>
        state is ApprovalState.Approved or ApprovalState.Rejected or ApprovalState.Cancelled or ApprovalState.Withdrawn or ApprovalState.Returned;

    /// <summary>What other modules already knew before an action: its state, whether it was authorised, who could act where.</summary>
    private sealed record Before(ApprovalState? State, bool Authorized, HashSet<(Guid StageId, string Person)> Tasks)
    {
        public static Before Nothing { get; } = new(null, false, []);

        public static Before Of(Loaded loaded) =>
            new(loaded.Instance.State, loaded.Instance.ProvisioningAuthorized, [.. TasksOf(loaded).Select(t => (t.StageId, t.Person))]);
    }

    /// <summary>A loaded instance: its summary row, the replayed engine, its route, and how much history was stored.</summary>
    internal sealed record Loaded(ApprovalInstanceRow Row, ApprovalInstance Instance, IReadOnlyList<StageDefinition> Stages, int StoredHistory);
}
