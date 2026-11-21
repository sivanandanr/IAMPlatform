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
/// only when this operation made the object; an object that was already there is never taken over. If a new account can't
/// be recorded after its steps ran, its entitlement is removed and it is disabled before the item fails.
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

        // A run that keeps stopping without finishing (an error the runner can't handle) fails for a person (DL-097).
        if (takeover && item.Attempts >= settings.MaxAttempts)
        {
            item.Fail("RunnerError", Now());
            await SaveAuditedAsync(item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        var operations = await db.Operations.Where(o => o.ItemId == itemId).OrderBy(o => o.Step).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (targets.Find(item.ConnectorKey ?? string.Empty) is not { } target)
        {
            item.Fail("ConnectorUnavailable", Now());
            await SaveAuditedAsync(item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // A new account recorded by a run that stopped before finishing the item: it is done (DL-096).
        if (item.NewAccountType is not null && await accounts.FindRecordedAsync(item.Id, cancellationToken).ConfigureAwait(false) is { } already)
        {
            item.Succeed(already, Now());
            await SaveAuditedAsync(item, ProvisioningAuditActions.Succeeded, AuditOutcome.Succeeded, null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Decided again from the verified approval and the frozen item; the stored steps must be exactly that plan (DL-096).
        var plan = operations.Where(o => o.Step <= ProvisioningPlanner.MaxPlanSteps).ToList();
        var fresh = await decider.DecideAsync(item.Id, item.ApprovalActions ?? 1, closed: true, Now(), cancellationToken).ConfigureAwait(false);
        var stop = fresh switch
        {
            null => "NoLongerAuthorized",
            { Item.State: ProvisioningState.Blocked } => ProvisioningItem.ProblemCode(fresh.Item.IntegrityProblem ?? fresh.Item.Problem, "Recheck"),
            _ when !ProvisioningPlanner.SamePlan(fresh.Operations, plan) => "PlanMismatch",
            _ => null,
        };
        if (stop is not null)
        {
            item.Fail(stop, Now());
            await SaveAuditedAsync(item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        foreach (var operation in plan.Where(o => o.State != ProvisioningState.Succeeded))
        {
            var step = Step(operation);
            var creates = operation.Action == ProvisioningPlanner.AccountCreate;
            var interrupted = operation.State == ProvisioningState.Running;
            operation.Start();

            // The lease is renewed with each step, under the item's row version: if another runner took the item over, this
            // run stops here rather than run the step a second time (DL-097).
            item.Renew(Now(), settings.Lease);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                return true;
            }

            // A step its runner was in the middle of may already be done: the target is read before it runs again. Not for a
            // create: an object being there doesn't prove this operation made it (DL-096), so the create runs again under its
            // operation id and the connector's ledger answers.
            var result = interrupted && !creates && await VerifyAsync(target, step, cancellationToken).ConfigureAwait(false)
                ? new StepResult(StepStatus.Done, "AlreadyInState")
                : await RunAsync(target, step, cancellationToken).ConfigureAwait(false);
            if (result.Status == StepStatus.Unknown)
            {
                result = !creates && await VerifyAsync(target, step, cancellationToken).ConfigureAwait(false)
                    ? new StepResult(StepStatus.Done, result.Code)
                    : new StepResult(StepStatus.Retry, result.Code ?? "Unknown");
            }

            // An account that was already there is someone's: never taken over (DL-083a, DL-096).
            if (creates && result is { Status: StepStatus.Done, Code: "AlreadyInState" })
            {
                result = new StepResult(StepStatus.Rejected, "TargetExists");
            }

            now = Now();
            switch (result.Status)
            {
                case StepStatus.Done:
                    operation.Complete(result.Code, now);
                    await SaveAuditedAsync(item, ProvisioningAuditActions.StepDone, AuditOutcome.Succeeded, operation, cancellationToken).ConfigureAwait(false);
                    break;

                case StepStatus.Retry when item.Attempts + 1 < settings.MaxAttempts:
                    operation.Release();
                    item.RetryAt(now + settings.DelayAfter(item.Attempts + 1));
                    await SaveAuditedAsync(item, ProvisioningAuditActions.StepRetry, AuditOutcome.Failed, operation, cancellationToken, result.Code).ConfigureAwait(false);
                    return true;

                default:
                    // Rejected, or temporary failures that ran out of attempts: a person must look at it.
                    operation.Fail(result.Code, now);
                    item.Fail(result.Status == StepStatus.Rejected ? ProvisioningItem.ProblemCode(result.Code, "Rejected") : "RetriesExhausted", now);
                    await SaveAuditedAsync(item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, operation, cancellationToken, result.Code).ConfigureAwait(false);
                    return true;
            }
        }

        // Every step is done: the target must now match the whole plan before anything is recorded.
        foreach (var operation in plan)
        {
            if (!await VerifyAsync(target, Step(operation), cancellationToken).ConfigureAwait(false))
            {
                item.Fail("VerifyMismatch", Now());
                await SaveAuditedAsync(item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, operation, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }

        Guid? recorded = null;
        if (item.NewAccountType is not null)
        {
            try
            {
                recorded = await accounts.RecordCreatedAsync(
                    new CreatedAccount(item.Id, item.ApplicationId!.Value, item.NewAccountType, item.AccountName!, plan[0].TargetId, item.Beneficiary!),
                    item.CorrelationId ?? MessageCorrelation.Current(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AccountNotRecordedException refused)
            {
                // The access exists on the target but can't be recorded: take it away before a person looks at it (DL-096).
                var undone = await UndoAsync(item, target, plan, operations, cancellationToken).ConfigureAwait(false);
                item.Fail(undone ? "AccountNotRecorded" : "UndoFailed", Now());
                await SaveAuditedAsync(item, ProvisioningAuditActions.Failed, AuditOutcome.Failed, null, cancellationToken, refused.Code).ConfigureAwait(false);
                return true;
            }
        }

        item.Succeed(recorded, Now());
        await SaveAuditedAsync(item, ProvisioningAuditActions.Succeeded, AuditOutcome.Succeeded, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Removes the entitlement and disables the new account (steps 11 and 12, fixed operation ids), each audited, then reads the
    /// target to confirm. Undo steps already stored must be exactly the expected ones, or nothing is run. True if undone.
    /// </summary>
    private async Task<bool> UndoAsync(ProvisioningItem item, IProvisioningTarget target, List<ProvisioningOperation> plan, List<ProvisioningOperation> operations, CancellationToken cancellationToken)
    {
        var expected = ProvisioningPlanner.UndoNewAccount(item.Id, plan);
        var stored = operations.Where(o => o.Step > ProvisioningPlanner.MaxPlanSteps).OrderBy(o => o.Step).ToList();
        if (stored.Count == 0)
        {
            db.Operations.AddRange(expected);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var result = await RunAsync(target, step, cancellationToken).ConfigureAwait(false);
            var done = result.Status == StepStatus.Done
                || (result.Status == StepStatus.Unknown && await VerifyAsync(target, step, cancellationToken).ConfigureAwait(false));
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
            undone &= await VerifyAsync(target, Step(operation), cancellationToken).ConfigureAwait(false);
        }

        return undone;

        static string Key(ProvisioningOperation o) => string.Join('|', o.Step, o.Id, o.Action, o.TargetId, o.RelatedId ?? "-");
    }

    private static TargetStep Step(ProvisioningOperation o) => new(
        o.Id,
        o.Action,
        o.TargetType,
        o.TargetId,
        o.RelatedType,
        o.RelatedId,
        JsonSerializer.Deserialize<Dictionary<string, string?>>(o.Attributes) ?? []);

    /// <summary>Runs a step within the step time limit; a call that breaks off or runs out of time (not shutdown) has an unknown outcome.</summary>
    private async Task<StepResult> RunAsync(IProvisioningTarget target, TargetStep step, CancellationToken cancellationToken)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(settings.StepTimeout);
            return await target.RunAsync(step, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new StepResult(StepStatus.Unknown, "CallFailed");
        }
    }

    /// <summary>Reads the target within the step time limit; a read that fails or runs out of time proves nothing, so it counts as not matching.</summary>
    private async Task<bool> VerifyAsync(IProvisioningTarget target, TargetStep step, CancellationToken cancellationToken)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(settings.StepTimeout);
            return await target.VerifyAsync(step, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
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
