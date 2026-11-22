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

namespace IAM.Modules.Provisioning.Infrastructure.Items;

/// <summary>
/// Runs planned items on their targets (DL-094). A runner claims a due item (Planned to Running, with a lease; the row
/// version makes two runners' claims conflict, and an expired lease lets another take over), then runs its steps in order,
/// saving each step's state before and after it, under the item's correlation id. A step that was running when a runner
/// stopped is checked on the target first. Temporary failures put the item back for later with a growing delay, until the
/// attempts run out; a rejection, a mismatch after the last step or an account the Account module won't record fails it for
/// a person. Only after every step is done and the target matches is a new account recorded and the item succeeded. Each
/// change is audited in its own transaction. Steps run under their fixed operation ids, so nothing is changed twice.
/// <para>
/// Since DL-096, before each run the approval and the plan are decided again from the verified approval and the frozen item
/// (<see cref="ProvisioningDecider"/>), and the stored steps must be exactly that plan: what the runner does never rests on its
/// own tables alone, and an approval, entitlement or account that changed since planning stops the run. A create is done
/// only when this operation made the object; an object that was already there is never taken over.
/// </para>
/// <para>
/// Since DL-099 every failure after a step ran goes one way (<c>FailAsync</c>): what this item did is undone first
/// (<see cref="ProvisioningPlanner.Undo"/>), under fixed operation ids, audited, resumed after a crash and read back; the item
/// fails with its reason, or <c>UndoFailed</c>. A target read that fails is retried, never taken as an answer; an approval that
/// is still being written postpones the run without counting an attempt; the lease is renewed before every step, the final
/// read and the recording.
/// </para>
/// </summary>
public sealed class ProvisioningRunner(
    ProvisioningDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IProvisioningTargets targets,
    IAccountProvisioning accounts,
    ProvisioningDecider decider,
    ProvisioningRunnerSettings settings)
{
    /// <summary>Runs up to one batch of due items; returns how many it claimed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = Now();
        var due = await db.Items.AsNoTracking()
            .Where(i => (i.State == ProvisioningState.Planned && (i.NextAttemptAtUtc == null || i.NextAttemptAtUtc <= now))
                || (i.State == ProvisioningState.Running && i.LeaseUntilUtc < now))
            .OrderBy(i => i.ReceivedAtUtc)
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
                // counts as an attempt, so it fails as RunnerError rather than being retried for ever.
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
        var operations = await db.Operations.Where(o => o.ItemId == itemId).OrderBy(o => o.Step).ToListAsync(cancellationToken).ConfigureAwait(false);
        var plan = operations.Where(o => o.Step <= ProvisioningPlanner.MaxPlanSteps).ToList();

        // A new account recorded by a run that stopped before finishing the item is done (DL-096), even on its last attempt
        // (DL-099): it is checked before the attempt limit.
        if (item.NewAccountType is not null && await accounts.FindRecordedAsync(item.Id, cancellationToken).ConfigureAwait(false) is { } already)
        {
            item.Succeed(already, Now());
            await SaveAuditedAsync(item, ProvisioningAuditActions.Succeeded, AuditOutcome.Succeeded, null, cancellationToken).ConfigureAwait(false);
            return true;
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
            var undoNeeded = ProvisioningPlanner.Undo(item.Id, plan).Count > 0 || operations.Any(o => o.Step > ProvisioningPlanner.MaxPlanSteps);
            var reason = lastAttempt ? "RunnerError" : "ConnectorUnavailable";
            item.Fail(undoNeeded ? "UndoFailed" : reason, Now());
            await SaveAuditedAsync(item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, null, cancellationToken, reason).ConfigureAwait(false);
            return true;
        }

        var run = new Run(item, target, plan, operations);

        // An undo a run stopped in the middle of is finished before anything else (DL-099).
        if (operations.Any(o => o.Step > ProvisioningPlanner.MaxPlanSteps))
        {
            await FailAsync(run, "Interrupted", null, null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // A run that keeps stopping without finishing (an error the runner can't handle) fails for a person (DL-097), after
        // whatever its steps did is undone (DL-099).
        if (lastAttempt)
        {
            await FailAsync(run, "RunnerError", null, null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Decided again from the verified approval and the frozen item; the stored steps must be exactly that plan, and the
        // stored item exactly its facts (DL-096, DL-099).
        ProvisioningPlan? fresh;
        try
        {
            fresh = await decider.DecideAsync(item.Id, item.ApprovalActions ?? 1, Now(), cancellationToken).ConfigureAwait(false);
        }
        catch (ApprovalChangingException)
        {
            // The approval is being written: nothing was tried, so no attempt is counted (DL-099).
            item.Postpone(Now() + settings.RetryDelay);
            await SaveAuditedAsync(item, ProvisioningAuditActions.StepRetry, AuditOutcome.Failed, null, cancellationToken, "ApprovalChanging").ConfigureAwait(false);
            return true;
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
            await FailAsync(run, stop, null, null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var facts = fresh!.Item;

        // An existing account's target id was given by a person when it was linked and isn't verified: the account must really
        // be on the target before anything is granted to it (DL-098). A read that fails proves nothing either way: try later.
        if (item.AccountId is not null)
        {
            switch (await ExistsAsync(target, ProvisioningPlanner.AccountObject, plan[0].TargetId, cancellationToken).ConfigureAwait(false))
            {
                case null:
                    await RetryOrFailAsync(run, null, "TargetUnreadable", cancellationToken).ConfigureAwait(false);
                    return true;
                case false:
                    await FailAsync(run, "AccountNotOnTarget", null, null, cancellationToken).ConfigureAwait(false);
                    return true;
            }
        }

        foreach (var operation in plan.Where(o => o.State != ProvisioningState.Succeeded))
        {
            var step = Step(operation);
            var creates = operation.Action == ProvisioningPlanner.AccountCreate;
            var interrupted = operation.State == ProvisioningState.Running;
            operation.Start();

            // The lease is renewed with each step, under the item's row version: if another runner took the item over, this
            // run stops here rather than run the step a second time (DL-097).
            if (!await RenewAsync(item, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            // A step its runner was in the middle of may already be done: the target is read before it runs again. Not for a
            // create: an object being there doesn't prove this operation made it (DL-096), so the create runs again under its
            // operation id and the connector's ledger answers.
            var result = interrupted && !creates && await VerifyAsync(target, step, cancellationToken).ConfigureAwait(false) == true
                ? new StepResult(StepStatus.Done, "AlreadyInState")
                : await RunAsync(target, step, cancellationToken).ConfigureAwait(false);
            if (result.Status == StepStatus.Unknown)
            {
                result = !creates && await VerifyAsync(target, step, cancellationToken).ConfigureAwait(false) == true
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
                    await SaveAuditedAsync(item, ProvisioningAuditActions.StepDone, AuditOutcome.Succeeded, operation, cancellationToken).ConfigureAwait(false);
                    break;

                case StepStatus.Retry:
                    await RetryOrFailAsync(run, operation, result.Code, cancellationToken).ConfigureAwait(false);
                    return true;

                default:
                    // Rejected: a person must look at it, after what earlier steps did is undone (DL-099).
                    operation.Fail(result.Code, Now());
                    await FailAsync(run, ProvisioningItem.ProblemCode(result.Code, "Rejected"), operation, result.Code, cancellationToken).ConfigureAwait(false);
                    return true;
            }
        }

        // Every step is done: the target must now match the whole plan before anything is recorded. The lease is renewed
        // first (DL-099); a read that fails proves nothing, so it is tried again later rather than failing the item.
        if (!await RenewAsync(item, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        foreach (var operation in plan)
        {
            switch (await VerifyAsync(target, Step(operation), cancellationToken).ConfigureAwait(false))
            {
                case null:
                    await RetryOrFailAsync(run, null, "TargetUnreadable", cancellationToken).ConfigureAwait(false);
                    return true;
                case false:
                    await FailAsync(run, "VerifyMismatch", operation, null, cancellationToken).ConfigureAwait(false);
                    return true;
            }
        }

        Guid? recorded = null;
        if (facts.NewAccountType is not null)
        {
            if (!await RenewAsync(item, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

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
                await FailAsync(run, "AccountNotRecorded", null, refused.Code, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The write may have committed before the error: read it back before undoing anything (DL-099).
                if (await accounts.FindRecordedAsync(item.Id, cancellationToken).ConfigureAwait(false) is not { } committed)
                {
                    await FailAsync(run, "RecordFailed", null, null, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                recorded = committed;
            }
        }

        item.Succeed(recorded, Now());
        await SaveAuditedAsync(item, ProvisioningAuditActions.Succeeded, AuditOutcome.Succeeded, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>One run's item, target and stored steps.</summary>
    private sealed record Run(ProvisioningItem Item, IProvisioningTarget Target, List<ProvisioningOperation> Plan, List<ProvisioningOperation> Operations);

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
        await FailAsync(run, "RetriesExhausted", operation, code, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one way an item fails once it has a connector (DL-099): what its steps did is undone first, then the item fails with
    /// <paramref name="reason"/>, or <c>UndoFailed</c> if the undo didn't take. If another runner took the item over meanwhile,
    /// this run stops and leaves the item to it.
    /// </summary>
    private async Task FailAsync(Run run, string reason, ProvisioningOperation? operation, string? code, CancellationToken cancellationToken)
    {
        var problem = reason;
        var undo = ProvisioningPlanner.Undo(run.Item.Id, run.Plan);
        if (undo.Count > 0 || run.Operations.Any(o => o.Step > ProvisioningPlanner.MaxPlanSteps))
        {
            switch (await UndoAsync(run, undo, reason, cancellationToken).ConfigureAwait(false))
            {
                case null:
                    return;
                case false:
                    problem = "UndoFailed";
                    break;
            }
        }

        run.Item.Fail(problem, Now());
        await SaveAuditedAsync(run.Item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, operation, cancellationToken, code ?? (problem == reason ? null : reason)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the undo steps (fixed operation ids), each audited, with the lease renewed before each, then reads the target to
    /// confirm. The steps are stored first, with the reason audited, so a run that stops half-way resumes them; steps already
    /// stored must be exactly the expected ones, or nothing is run. True if undone; null if another runner took the item over.
    /// </summary>
    private async Task<bool?> UndoAsync(Run run, IReadOnlyList<ProvisioningOperation> expected, string reason, CancellationToken cancellationToken)
    {
        var item = run.Item;
        var stored = run.Operations.Where(o => o.Step > ProvisioningPlanner.MaxPlanSteps).OrderBy(o => o.Step).ToList();
        if (stored.Count == 0)
        {
            db.Operations.AddRange(expected);
            run.Operations.AddRange(expected);
            item.Renew(Now(), settings.Lease);
            try
            {
                await SaveAuditedAsync(item, ProvisioningAuditActions.StepUndone, AuditOutcome.Failed, null, cancellationToken, reason).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                return null;
            }

            stored = [.. expected];
        }
        else if (!stored.Select(Key).SequenceEqual(expected.Select(Key), StringComparer.Ordinal))
        {
            return false;
        }

        var undone = true;
        foreach (var operation in stored.Where(o => o.State != ProvisioningState.Succeeded))
        {
            if (operation.State == ProvisioningState.Failed)
            {
                undone = false;
                continue;
            }

            var step = Step(operation);
            operation.Start();
            if (!await RenewAsync(item, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var result = await RunAsync(run.Target, step, cancellationToken).ConfigureAwait(false);
            var done = result.Status == StepStatus.Done
                || (result.Status == StepStatus.Unknown && await VerifyAsync(run.Target, step, cancellationToken).ConfigureAwait(false) == true);
            if (done)
            {
                operation.Complete(result.Code, Now());
            }
            else
            {
                operation.Fail(result.Code, Now());
                undone = false;
            }

            await SaveAuditedAsync(item, ProvisioningAuditActions.StepUndone, done ? AuditOutcome.Succeeded : AuditOutcome.Failed, operation, cancellationToken, result.Code).ConfigureAwait(false);
        }

        foreach (var operation in stored)
        {
            undone &= await VerifyAsync(run.Target, Step(operation), cancellationToken).ConfigureAwait(false) == true;
        }

        return undone;

        static string Key(ProvisioningOperation o) => string.Join('|', o.Step, o.Id, o.Action, o.TargetId, o.RelatedId ?? "-");
    }

    /// <summary>Extends the lease under the item's row version; false (and nothing more is done) if another runner took it over.</summary>
    private async Task<bool> RenewAsync(ProvisioningItem item, CancellationToken cancellationToken)
    {
        item.Renew(Now(), settings.Lease);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
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
    /// A connector call within the step time limit (DL-097). The limit is a hard one (DL-099): a call that ignores cancellation
    /// is abandoned when it runs out, and this run carries on.
    /// </summary>
    private async Task<T> WithinLimitAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(settings.StepTimeout);
        return await call(limit.Token).WaitAsync(settings.StepTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a step within the step time limit; a call that breaks off or runs out of time (not shutdown) has an unknown outcome.</summary>
    private async Task<StepResult> RunAsync(IProvisioningTarget target, TargetStep step, CancellationToken cancellationToken)
    {
        try
        {
            return await WithinLimitAsync(ct => target.RunAsync(step, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new StepResult(StepStatus.Unknown, "CallFailed");
        }
    }

    /// <summary>Whether an object is on the target, within the step time limit; null if the read failed (it proves nothing).</summary>
    private async Task<bool?> ExistsAsync(IProvisioningTarget target, string objectType, string id, CancellationToken cancellationToken)
    {
        try
        {
            return await WithinLimitAsync(ct => target.ExistsAsync(objectType, id, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Whether the target matches a step, within the step time limit; null if the read failed (it proves nothing).</summary>
    private async Task<bool?> VerifyAsync(IProvisioningTarget target, TargetStep step, CancellationToken cancellationToken)
    {
        try
        {
            return await WithinLimitAsync(ct => target.VerifyAsync(step, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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
        }

        payload.Add(Field("code", ProvisioningItem.ProblemCode(code ?? operation?.ResultCode, "none")));
        if (item.RecordedAccountId is { } account)
        {
            payload.Add(Field("recordedAccountId", account.ToString()));
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
}
