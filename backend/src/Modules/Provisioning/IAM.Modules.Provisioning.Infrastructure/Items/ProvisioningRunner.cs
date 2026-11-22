using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Account.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace IAM.Modules.Provisioning.Infrastructure.Items;

/// <summary>
/// Runs planned items on their targets (DL-094). A runner claims a due item (Planned to Running, with a lease; the row
/// version makes two runners' claims conflict, and an expired lease lets another take over), then runs its steps in order,
/// saving each step's state before and after it, under the item's correlation id. A step that was running when a runner
/// stopped is checked on the target first. Temporary failures put the item back for later with a growing delay, until the
/// attempts run out. Only after every step is done and the target matches is a new account recorded and the item succeeded.
/// Each change is audited in its own transaction. Steps run under their fixed operation ids, so nothing is changed twice.
/// <para>
/// Since DL-096, before each run the approval and the plan are decided again from the verified approval and the frozen item
/// (<see cref="ProvisioningDecider"/>), and the stored steps and facts must be exactly that (DL-099). A create is done only
/// when this operation made the object; an object that was already there is never taken over.
/// </para>
/// <para>
/// Since DL-099 every failure after a step ran is undone first. Since DL-100 an undo trusts nothing in this module's own
/// tables alone: each step it reverses must have its audited <c>step_done</c> record (operation id, fingerprint, code
/// Applied), and its targets must be in the application's containers or be the Account module's own target id for the
/// account; otherwise it calls nothing and the item fails UndoFailed. An entitlement another item still holds is kept (the
/// account and entitlement are locked while either changes them). A temporary failure during an undo retries it, up to its
/// own limit. Every target read is three-way (matches, doesn't, unknown); an unknown read is never taken as an answer. The
/// lease is renewed before every call to the target, and a call that runs out of time is cancelled before it is abandoned.
/// </para>
/// </summary>
public sealed partial class ProvisioningRunner(
    ProvisioningDbContext db,
    IAuditWriter audit,
    IAuditTrail trail,
    TimeProvider clock,
    IProvisioningTargets targets,
    IAccountProvisioning accounts,
    IAccountDirectory accountDirectory,
    ConnectorBindings bindings,
    ProvisioningDecider decider,
    ProvisioningRunnerSettings settings,
    ILogger<ProvisioningRunner> logger)
{
    /// <summary>The code of a removal skipped because another item still holds the access (DL-100).</summary>
    public const string KeptForOtherItem = "KeptForOtherItem";

    /// <summary>The most audit records read for one item's undo check.</summary>
    public const int MaxAuditRecords = 1_000;

    /// <summary>Runs up to one batch of due items; returns how many it claimed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = Now();

        // Taken in the order they fell due (DL-100), so an item that keeps failing doesn't come first every time.
        var due = await db.Items.AsNoTracking()
            .Where(i => (i.State == ProvisioningState.Planned && (i.NextAttemptAtUtc == null || i.NextAttemptAtUtc <= now))
                || (i.State == ProvisioningState.Running && i.LeaseUntilUtc < now))
            .OrderBy(i => i.State == ProvisioningState.Running ? i.LeaseUntilUtc : (i.NextAttemptAtUtc ?? i.ReceivedAtUtc))
            .Select(i => i.Id)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var claimed = 0;
        foreach (var id in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await RunItemAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    claimed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One item's failure doesn't stop the batch (DL-097). It stays running until its lease runs out; taking it over
                // counts as an attempt (or an undo pass), so it ends for a person rather than being retried for ever.
                db.ChangeTracker.Clear();
            }
        }

        return claimed;
    }

    /// <summary>Claims and runs one item; false if it isn't due or another runner claimed it first.</summary>
    public async Task<bool> RunItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var now = Now();
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken).ConfigureAwait(false);
        if (item is null || !item.CanClaim(now))
        {
            return false;
        }

        var takeover = item.State == ProvisioningState.Running;
        item.Claim(now, settings.Lease);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }

        // Everything this run audits carries the request's correlation id (DL-094).
        using var trace = MessageCorrelation.StartFor(item.CorrelationId, "iam.provisioning.run");
        try
        {
            await RunClaimedAsync(item, takeover, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another runner took the item over (its lease ran out): this run stops and leaves it to that one (DL-097).
            db.ChangeTracker.Clear();
        }

        return true;
    }

    private async Task RunClaimedAsync(ProvisioningItem item, bool takeover, CancellationToken cancellationToken)
    {
        var operations = await db.Operations.Where(o => o.ItemId == item.Id).OrderBy(o => o.Step).ToListAsync(cancellationToken).ConfigureAwait(false);
        var plan = operations.Where(o => o.Step <= ProvisioningPlanner.MaxPlanSteps).ToList();
        var undoing = item.UndoReason is not null || operations.Any(o => o.Step > ProvisioningPlanner.MaxPlanSteps);

        // A new account recorded by a run that stopped before finishing is done (DL-096), even on its last attempt (DL-099);
        // but not if its access was being undone meanwhile: the record and the target disagree, for a person (DL-100).
        if (item.NewAccountType is not null && await accounts.FindRecordedAsync(item.Id, cancellationToken).ConfigureAwait(false) is { } already)
        {
            if (undoing)
            {
                item.Fail("RecordedAfterUndo", Now());
                await SaveAuditedAsync(item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            item.Succeed(already, Now());
            await SaveAuditedAsync(item, ProvisioningAuditActions.Succeeded, AuditOutcome.Succeeded, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var lastAttempt = takeover && item.Attempts >= settings.MaxAttempts;
        IProvisioningTarget? found;
        try
        {
            found = targets.Find(item.ConnectorKey ?? string.Empty);
        }
        catch (Exception ex) when (lastAttempt && ex is not OperationCanceledException)
        {
            // The error that kept stopping the run: it ends here (DL-097).
            found = null;
        }

        if (found is not { } target)
        {
            // Nothing can be run or undone without the connector: a person must look at it, and at anything its steps did.
            var undoNeeded = undoing || ProvisioningPlanner.Undo(item.Id, plan).Count > 0;
            var reason = lastAttempt ? "RunnerError" : "ConnectorUnavailable";
            item.Fail(undoNeeded ? "UndoFailed" : reason, Now());
            await SaveAuditedAsync(item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, null, cancellationToken, reason).ConfigureAwait(false);
            return;
        }

        var run = new Run(item, target, plan, operations);

        // An undo under way is finished before anything else, with the reason it started for (DL-099, DL-100).
        if (undoing)
        {
            await UndoAndFailAsync(run, item.UndoReason ?? "Interrupted", null, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A run that keeps stopping without finishing fails for a person (DL-097), after what its steps did is undone (DL-099).
        if (lastAttempt)
        {
            await UndoAndFailAsync(run, "RunnerError", null, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Decided again from the verified approval and the frozen item; the stored steps and facts must be exactly that.
        ProvisioningPlan? fresh;
        try
        {
            fresh = await decider.DecideAsync(item.Id, item.ApprovalActions ?? 1, Now(), cancellationToken).ConfigureAwait(false);
        }
        catch (ApprovalChangingException)
        {
            // The approval is being written: nothing was tried, so no attempt is counted (DL-099), up to a limit (DL-100).
            if (item.Postponements >= settings.MaxPostponements)
            {
                await RetryOrFailAsync(run, null, "ApprovalChanging", cancellationToken).ConfigureAwait(false);
                return;
            }

            item.Postpone(Now() + settings.RetryDelay);
            await SaveAuditedAsync(item, ProvisioningAuditActions.StepRetry, AuditOutcome.Failed, null, cancellationToken, "ApprovalChanging").ConfigureAwait(false);
            return;
        }

        var stop = fresh switch
        {
            null => "NoLongerAuthorized",
            { Item.State: ProvisioningState.Blocked } => ProvisioningItem.ProblemCode(fresh.Item.IntegrityProblem ?? fresh.Item.Problem, "Recheck"),
            _ when !ProvisioningPlanner.SamePlan(fresh.Operations, plan) || !SameFacts(item, fresh.Item) => "PlanMismatch",
            _ => null,
        };
        if (stop is not null)
        {
            await UndoAndFailAsync(run, stop, null, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var facts = fresh!.Item;

        // An existing account must really be on the target before anything is granted to it (DL-098); a read that fails
        // proves nothing either way: try later.
        if (item.AccountId is not null)
        {
            switch (await ExistsAsync(run, ProvisioningPlanner.AccountObject, plan[0].TargetId, cancellationToken).ConfigureAwait(false))
            {
                case null:
                    await RetryOrFailAsync(run, null, "TargetUnreadable", cancellationToken).ConfigureAwait(false);
                    return;
                case false:
                    await UndoAndFailAsync(run, "AccountNotOnTarget", null, null, cancellationToken).ConfigureAwait(false);
                    return;
            }
        }

        foreach (var operation in plan.Where(o => o.State != ProvisioningState.Succeeded))
        {
            // An entitlement on an existing account is changed by one item at a time (DL-100).
            var locked = operation.Action == ProvisioningPlanner.EntitlementAdd && item.AccountId is not null;
            if (locked && !await TakeGrantLockAsync(item, cancellationToken).ConfigureAwait(false))
            {
                await RetryOrFailAsync(run, null, "GrantBusy", cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                if (!await RunPlanStepAsync(run, operation, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            finally
            {
                if (locked)
                {
                    await ReleaseGrantLockAsync(item).ConfigureAwait(false);
                }
            }
        }

        // Every step is done: the target must now match the whole plan before anything is recorded. A read that fails proves
        // nothing, so it is tried again later rather than failing the item (DL-099, DL-100).
        foreach (var operation in plan)
        {
            switch (await VerifyAsync(run, Step(operation), cancellationToken).ConfigureAwait(false))
            {
                case null:
                    await RetryOrFailAsync(run, null, "TargetUnreadable", cancellationToken).ConfigureAwait(false);
                    return;
                case false:
                    await UndoAndFailAsync(run, "VerifyMismatch", operation, null, cancellationToken).ConfigureAwait(false);
                    return;
            }
        }

        Guid? recorded = null;
        if (facts.NewAccountType is not null)
        {
            await RenewOrStopAsync(item, cancellationToken).ConfigureAwait(false);
            try
            {
                // From the decided facts, never the runner's own row alone (DL-099).
                recorded = await accounts.RecordCreatedAsync(
                    new CreatedAccount(item.Id, facts.ApplicationId!.Value, facts.NewAccountType, facts.AccountName!, plan[0].TargetId, facts.Beneficiary!),
                    item.CorrelationId ?? MessageCorrelation.Current(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AccountNotRecordedException refused)
            {
                // The access exists on the target but can't be recorded: take it away before a person looks at it (DL-096).
                await UndoAndFailAsync(run, "AccountNotRecorded", null, refused.Code, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The write may have committed before the error: read it back before undoing anything (DL-099).
                if (await accounts.FindRecordedAsync(item.Id, cancellationToken).ConfigureAwait(false) is not { } committed)
                {
                    await UndoAndFailAsync(run, "RecordFailed", null, null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                recorded = committed;
            }
        }

        item.Succeed(recorded, Now());
        await SaveAuditedAsync(item, ProvisioningAuditActions.Succeeded, AuditOutcome.Succeeded, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one planned step; false if the item was put back or failed (the run ends).</summary>
    private async Task<bool> RunPlanStepAsync(Run run, ProvisioningOperation operation, CancellationToken cancellationToken)
    {
        var step = Step(operation);
        var creates = operation.Action == ProvisioningPlanner.AccountCreate;
        var interrupted = operation.State == ProvisioningState.Running;
        operation.Start();

        // A step its runner was in the middle of may already be done: the target is read before it runs again. Not for a
        // create: an object being there doesn't prove this operation made it (DL-096), so the create runs again under its
        // operation id and the connector's ledger answers.
        var result = interrupted && !creates && await VerifyAsync(run, step, cancellationToken).ConfigureAwait(false) == true
            ? new StepResult(StepStatus.Done, "AlreadyInState")
            : await RunStepAsync(run, step, cancellationToken).ConfigureAwait(false);
        if (result.Status == StepStatus.Unknown)
        {
            result = !creates && await VerifyAsync(run, step, cancellationToken).ConfigureAwait(false) == true
                ? new StepResult(StepStatus.Done, result.Code)
                : new StepResult(StepStatus.Retry, result.Code ?? "Unknown");
        }

        // An account that was already there is someone's: never taken over (DL-083a, DL-096).
        if (creates && result is { Status: StepStatus.Done, Code: "AlreadyInState" })
        {
            result = new StepResult(StepStatus.Rejected, "TargetExists");
        }

        switch (result.Status)
        {
            case StepStatus.Done:
                operation.Complete(result.Code, Now());
                await SaveAuditedAsync(run.Item, ProvisioningAuditActions.StepDone, AuditOutcome.Succeeded, operation, cancellationToken).ConfigureAwait(false);
                return true;

            case StepStatus.Retry:
                await RetryOrFailAsync(run, operation, result.Code, cancellationToken).ConfigureAwait(false);
                return false;

            default:
                // Rejected: a person must look at it, after what earlier steps did is undone (DL-099).
                operation.Fail(result.Code, Now());
                await UndoAndFailAsync(run, ProvisioningItem.ProblemCode(result.Code, "Rejected"), operation, result.Code, cancellationToken).ConfigureAwait(false);
                return false;
        }
    }

    /// <summary>One run's item, target and stored steps.</summary>
    private sealed record Run(ProvisioningItem Item, IProvisioningTarget Target, List<ProvisioningOperation> Plan, List<ProvisioningOperation> Operations);

    private enum UndoOutcome
    {
        Done = 1,
        Failed = 2,
        Later = 3,
    }

    /// <summary>Whether the stored item says exactly what the item decided again says (DL-099): the runner never acts on its own row alone.</summary>
    public static bool SameFacts(ProvisioningItem stored, ProvisioningItem fresh)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(fresh);
        return string.Equals(stored.Beneficiary, fresh.Beneficiary, StringComparison.Ordinal)
            && stored.ApplicationId == fresh.ApplicationId
            && stored.EntitlementId == fresh.EntitlementId
            && stored.EntitlementVersionId == fresh.EntitlementVersionId
            && stored.AccountId == fresh.AccountId
            && string.Equals(stored.NewAccountType, fresh.NewAccountType, StringComparison.Ordinal)
            && string.Equals(stored.AccountName, fresh.AccountName, StringComparison.Ordinal)
            && string.Equals(stored.ConnectorKey, fresh.ConnectorKey, StringComparison.Ordinal);
    }

    /// <summary>A temporary failure: back to planned with a growing delay, or failed (and undone) once the attempts run out.</summary>
    private async Task RetryOrFailAsync(Run run, ProvisioningOperation? operation, string? code, CancellationToken cancellationToken)
    {
        if (run.Item.Attempts + 1 < settings.MaxAttempts)
        {
            operation?.Release();
            run.Item.RetryAt(Now() + settings.DelayAfter(run.Item.Attempts + 1));
            await SaveAuditedAsync(run.Item, ProvisioningAuditActions.StepRetry, AuditOutcome.Failed, operation, cancellationToken, code).ConfigureAwait(false);
            return;
        }

        operation?.Fail(code, Now());
        await UndoAndFailAsync(run, "RetriesExhausted", operation, code, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one way an item fails once it has a connector (DL-099): what its steps did is undone first, then the item fails with
    /// the reason the undo started for, or <c>UndoFailed</c> if the undo couldn't be trusted, was refused or ran out of passes.
    /// An undo that failed for now is tried again later (DL-100).
    /// </summary>
    private async Task UndoAndFailAsync(Run run, string reason, ProvisioningOperation? operation, string? code, CancellationToken cancellationToken)
    {
        var item = run.Item;
        var undo = ProvisioningPlanner.Undo(item.Id, run.Plan);
        if (undo.Count == 0 && item.UndoReason is null && !run.Operations.Any(o => o.Step > ProvisioningPlanner.MaxPlanSteps))
        {
            item.Fail(reason, Now());
            await SaveAuditedAsync(item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, operation, cancellationToken, code).ConfigureAwait(false);
            return;
        }

        switch (await UndoAsync(run, undo, reason, cancellationToken).ConfigureAwait(false))
        {
            case UndoOutcome.Done:
                item.Fail(item.UndoReason ?? reason, Now());
                await SaveAuditedAsync(item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, operation, cancellationToken, code).ConfigureAwait(false);
                break;
            case UndoOutcome.Later:
                item.RetryUndoAt(Now() + settings.DelayAfter(item.UndoAttempts));
                await SaveAuditedAsync(item, ProvisioningAuditActions.StepRetry, AuditOutcome.Failed, null, cancellationToken, "UndoRetry").ConfigureAwait(false);
                break;
            default:
                item.Fail("UndoFailed", Now());
                await SaveAuditedAsync(item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, operation, cancellationToken, item.UndoReason ?? reason).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Runs the undo steps (fixed operation ids), each audited, then reads the target to confirm. The steps are stored first,
    /// with the reason audited, so a run that stops half-way resumes them; steps already stored must be exactly the expected
    /// ones. Nothing is called unless the undo is trusted (DL-100). A removal another item still needs is skipped.
    /// </summary>
    private async Task<UndoOutcome> UndoAsync(Run run, IReadOnlyList<ProvisioningOperation> expected, string reason, CancellationToken cancellationToken)
    {
        var item = run.Item;
        if (item.UndoAttempts >= settings.UndoMaxAttempts)
        {
            return UndoOutcome.Failed;
        }

        var stored = run.Operations.Where(o => o.Step > ProvisioningPlanner.MaxPlanSteps).OrderBy(o => o.Step).ToList();
        if (stored.Count == 0)
        {
            db.Operations.AddRange(expected);
            run.Operations.AddRange(expected);
            stored = [.. expected];
        }
        else if (!stored.Select(Key).SequenceEqual(expected.Select(Key), StringComparer.Ordinal))
        {
            return UndoOutcome.Failed;
        }

        item.BeginUndoPass(reason);
        item.Renew(Now(), settings.Lease);
        await SaveAuditedAsync(item, ProvisioningAuditActions.StepUndone, AuditOutcome.Failed, null, cancellationToken, item.UndoReason).ConfigureAwait(false);

        if (stored.Count == 0 || !await TrustedAsync(run, stored, cancellationToken).ConfigureAwait(false))
        {
            return UndoOutcome.Failed;
        }

        foreach (var operation in stored.Where(o => o.State != ProvisioningState.Succeeded))
        {
            if (operation.State == ProvisioningState.Failed)
            {
                return UndoOutcome.Failed;
            }

            var removes = operation.Action == ProvisioningPlanner.EntitlementRemove && item.AccountId is not null;
            if (removes && !await TakeGrantLockAsync(item, cancellationToken).ConfigureAwait(false))
            {
                return UndoOutcome.Later;
            }

            try
            {
                if (removes && await HeldElsewhereAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    // Another item granted the same access and still holds it: it stays (DL-100).
                    operation.Start();
                    await RenewOrStopAsync(item, cancellationToken).ConfigureAwait(false);
                    operation.Complete(KeptForOtherItem, Now());
                    await SaveAuditedAsync(item, ProvisioningAuditActions.StepUndone, AuditOutcome.Succeeded, operation, cancellationToken, KeptForOtherItem).ConfigureAwait(false);
                    continue;
                }

                var step = Step(operation);
                operation.Start();
                var result = await RunStepAsync(run, step, cancellationToken).ConfigureAwait(false);
                if (result.Status == StepStatus.Unknown && await VerifyAsync(run, step, cancellationToken).ConfigureAwait(false) == true)
                {
                    result = new StepResult(StepStatus.Done, result.Code);
                }

                switch (result.Status)
                {
                    case StepStatus.Done:
                        operation.Complete(result.Code, Now());
                        await SaveAuditedAsync(item, ProvisioningAuditActions.StepUndone, AuditOutcome.Succeeded, operation, cancellationToken, result.Code).ConfigureAwait(false);
                        break;
                    case StepStatus.Rejected:
                        operation.Fail(result.Code, Now());
                        await SaveAuditedAsync(item, ProvisioningAuditActions.StepUndone, AuditOutcome.Failed, operation, cancellationToken, result.Code).ConfigureAwait(false);
                        return UndoOutcome.Failed;
                    default:
                        // Failed for now, or an outcome no read could settle: tried again later (DL-100).
                        operation.Release();
                        await SaveAuditedAsync(item, ProvisioningAuditActions.StepUndone, AuditOutcome.Failed, operation, cancellationToken, result.Code ?? "Unknown").ConfigureAwait(false);
                        return UndoOutcome.Later;
                }
            }
            finally
            {
                if (removes)
                {
                    await ReleaseGrantLockAsync(item).ConfigureAwait(false);
                }
            }
        }

        foreach (var operation in stored.Where(o => o.ResultCode != KeptForOtherItem))
        {
            switch (await VerifyAsync(run, Step(operation), cancellationToken).ConfigureAwait(false))
            {
                case null:
                    return UndoOutcome.Later;
                case false:
                    return UndoOutcome.Failed;
            }
        }

        return UndoOutcome.Done;

        static string Key(ProvisioningOperation o) => string.Join('|', o.Step, o.Id, o.Action, o.TargetId, o.RelatedId ?? "-");
    }

    /// <summary>
    /// Whether an undo may touch the target (DL-100): every step it reverses has exactly one audited <c>step_done</c> record
    /// with its operation id, fingerprint and code Applied, and none of its records is damaged; a new account is the item's own
    /// account in the application's account container; an existing account is the Account module's target id for the item's
    /// account; the group is in the application's group container; and each undo step names exactly those objects.
    /// </summary>
    private async Task<bool> TrustedAsync(Run run, List<ProvisioningOperation> undo, CancellationToken cancellationToken)
    {
        var item = run.Item;
        if (item.ApplicationId is not { } applicationId || bindings.For(applicationId) is not { } binding)
        {
            return false;
        }

        var create = run.Plan.Where(o => o.Action == ProvisioningPlanner.AccountCreate).ToList();
        var add = run.Plan.Where(o => o.Action == ProvisioningPlanner.EntitlementAdd).ToList();
        if (create.Count > 1 || add.Count != 1 || add[0].RelatedId is not { } group || !ProvisioningPlanner.IsInGroupContainer(group, binding))
        {
            return false;
        }

        string account;
        List<ProvisioningOperation> reversed;
        if (create.Count == 1)
        {
            if (item.AccountId is not null || item.AccountName is null
                || !string.Equals(create[0].TargetId, ProvisioningPlanner.NewAccountTarget(item.AccountName, binding), StringComparison.Ordinal))
            {
                return false;
            }

            account = create[0].TargetId;
            reversed = [create[0], add[0]];
        }
        else
        {
            if (item.AccountId is not { } accountId
                || await accountDirectory.FindAsync(accountId, cancellationToken).ConfigureAwait(false) is not { } known
                || known.ApplicationId != applicationId
                || !string.Equals(known.TargetId, add[0].TargetId, StringComparison.Ordinal))
            {
                return false;
            }

            account = add[0].TargetId;
            reversed = [add[0]];
        }

        if (!undo.All(o => string.Equals(o.TargetId, account, StringComparison.Ordinal)
            && (o.Action != ProvisioningPlanner.EntitlementRemove || string.Equals(o.RelatedId, group, StringComparison.Ordinal))))
        {
            return false;
        }

        IReadOnlyList<AuditTrailEntry> entries;
        try
        {
            entries = await trail.ForTargetAsync(ProvisioningAuditActions.Target, item.Id.ToString(), MaxAuditRecords, cancellationToken).ConfigureAwait(false);
        }
        catch (AuditTrailTooLongException)
        {
            return false;
        }

        return reversed.All(o => o.ResultCode == ProvisioningPlanner.Applied
            && entries.Count(e => e.Action == ProvisioningAuditActions.StepDone && Says(e, "operationId", o.Id.ToString())
                && Says(e, "fingerprint", ProvisioningPlanner.Fingerprint(o)) && Says(e, "code", ProvisioningPlanner.Applied)) == 1
            && entries.Where(e => Says(e, "operationId", o.Id.ToString())).All(e => e.Intact));

        static bool Says(AuditTrailEntry entry, string field, string value) =>
            entry.Payload.TryGetValue(field, out var text) && string.Equals(text, value, StringComparison.Ordinal);
    }

    /// <summary>Whether another item still holds the item's entitlement on its account: succeeded, or running and not being undone.</summary>
    private Task<bool> HeldElsewhereAsync(ProvisioningItem item, CancellationToken cancellationToken) =>
        db.Items.AsNoTracking().AnyAsync(
            i => i.Id != item.Id && i.AccountId == item.AccountId && i.EntitlementId == item.EntitlementId
                && (i.State == ProvisioningState.Succeeded || (i.State == ProvisioningState.Running && i.UndoReason == null)),
            cancellationToken);

    /// <summary>Takes the item's account and entitlement for one change (DL-100); false if another item holds them now.</summary>
    private async Task<bool> TakeGrantLockAsync(ProvisioningItem item, CancellationToken cancellationToken)
    {
        if (item.AccountId is not { } account || item.EntitlementId is not { } entitlement)
        {
            return false;
        }

        var now = Now();
        var until = now + settings.Lease;
        var holder = item.Id;
        var taken = await db.GrantLocks
            .Where(l => l.AccountId == account && l.EntitlementId == entitlement && (l.HeldBy == holder || l.UntilUtc < now))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.HeldBy, holder).SetProperty(l => l.UntilUtc, until), cancellationToken)
            .ConfigureAwait(false);
        if (taken == 1)
        {
            return true;
        }

        if (await db.GrantLocks.AnyAsync(l => l.AccountId == account && l.EntitlementId == entitlement, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var row = GrantLock.Take(account, entitlement, holder, until);
        db.GrantLocks.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            // Another item took it first.
            return false;
        }
        finally
        {
            db.Entry(row).State = EntityState.Detached;
        }
    }

    private async Task ReleaseGrantLockAsync(ProvisioningItem item)
    {
        var released = Now().AddSeconds(-1);
        var holder = item.Id;
        await db.GrantLocks
            .Where(l => l.AccountId == item.AccountId && l.EntitlementId == item.EntitlementId && l.HeldBy == holder)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.UntilUtc, released), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Extends the lease (and saves what is pending) under the item's row version; another runner's takeover stops this run.</summary>
    private async Task RenewOrStopAsync(ProvisioningItem item, CancellationToken cancellationToken)
    {
        item.Renew(Now(), settings.Lease);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TargetStep Step(ProvisioningOperation o) => new(
        o.Id,
        o.Action,
        o.TargetType,
        o.TargetId,
        o.RelatedType,
        o.RelatedId,
        JsonSerializer.Deserialize<Dictionary<string, string?>>(o.Attributes) ?? []);

    /// <summary>
    /// A connector call within the step time limit (DL-097), after renewing the lease (DL-100). The limit is a hard one: a call
    /// that runs out of time is cancelled and abandoned, and any later fault of it is logged rather than lost.
    /// </summary>
    private async Task<T> CallAsync<T>(ProvisioningItem item, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        await RenewOrStopAsync(item, cancellationToken).ConfigureAwait(false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(settings.StepTimeout);
        var task = call(limit.Token);
        try
        {
            return await task.WaitAsync(settings.StepTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await limit.CancelAsync().ConfigureAwait(false);
            var itemId = item.Id;
            _ = task.ContinueWith(
                t => LogAbandonedCallFailed(logger, itemId, t.Exception?.GetBaseException().GetType().Name ?? "None"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>Runs a step; a call that breaks off or runs out of time (not shutdown) has an unknown outcome.</summary>
    private async Task<StepResult> RunStepAsync(Run run, TargetStep step, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(run.Item, ct => run.Target.RunAsync(step, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not DbUpdateConcurrencyException && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            return new StepResult(StepStatus.Unknown, "CallFailed");
        }
    }

    /// <summary>Whether an object is on the target; null if the read failed (it proves nothing).</summary>
    private async Task<bool?> ExistsAsync(Run run, string objectType, string id, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(run.Item, ct => run.Target.ExistsAsync(objectType, id, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not DbUpdateConcurrencyException && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Whether the target matches a step; null if the read failed (it proves nothing).</summary>
    private async Task<bool?> VerifyAsync(Run run, TargetStep step, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(run.Item, ct => run.Target.VerifyAsync(step, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not DbUpdateConcurrencyException && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task SaveAuditedAsync(ProvisioningItem item, string action, AuditOutcome outcome, ProvisioningOperation? operation, CancellationToken cancellationToken, string? code = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var cause = Activity.Current?.TraceId.ToHexString();
        var correlation = item.CorrelationId ?? MessageCorrelation.Current();
        List<AuditPayloadField> payload =
        [
            Field("state", item.State.ToString()),
            Field("attempts", item.Attempts.ToString(CultureInfo.InvariantCulture)),
            Field("connector", item.ConnectorKey ?? "none"),
        ];
        if (operation is not null)
        {
            payload.Add(Field("step", operation.Step.ToString(CultureInfo.InvariantCulture)));
            payload.Add(Field("operationId", operation.Id.ToString()));
            payload.Add(Field("operation", operation.Action));
            payload.Add(Field("stepState", operation.State.ToString()));

            // Which objects the step names, as a hash (DL-100): an undo checks a stored step against it.
            payload.Add(Field("fingerprint", ProvisioningPlanner.Fingerprint(operation)));
        }

        payload.Add(Field("code", ProvisioningItem.ProblemCode(code ?? operation?.ResultCode, "none")));
        if (item.RecordedAccountId is { } account)
        {
            payload.Add(Field("recordedAccountId", account.ToString()));
        }

        if (item.UndoAttempts > 0)
        {
            payload.Add(Field("undoAttempts", item.UndoAttempts.ToString(CultureInfo.InvariantCulture)));
        }

        await audit.AppendAsync(
            tx.GetDbTransaction(),
            new AuditEventRecord
            {
                Action = action,
                TargetType = ProvisioningAuditActions.Target,
                TargetId = item.Id.ToString(),
                Outcome = outcome,
                Reason = item.State == ProvisioningState.Failed ? item.Problem : null,
                OccurredAtUtc = Now(),
                CorrelationId = correlation,
                CausationId = cause is not null && cause != correlation ? cause : null,
                Payload = payload,
            },
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private DateTimeOffset Now() => UtcMicrosecondConverter.TruncateToMicroseconds(clock.GetUtcNow());

    private static AuditPayloadField Field(string name, string value) => new(name, value, AuditFieldClass.Plain);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A target call abandoned at its time limit for item {ItemId} later failed ({ErrorType}).")]
    private static partial void LogAbandonedCallFailed(ILogger logger, Guid itemId, string errorType);
}
