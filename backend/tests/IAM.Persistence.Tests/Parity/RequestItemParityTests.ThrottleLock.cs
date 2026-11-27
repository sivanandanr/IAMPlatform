using IAM.Connectors.Sdk;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Workers.Provisioning;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 9 slice 9 (DL-104), the DL-103 re-review's findings, on both engines: a connector's own Throttled after its write
/// landed is unknown (only the SDK's RateLimited is a plain retry); a success is never saved over a grant lock another run took;
/// waiting for a busy grant counts no attempt; and an undo inside a run keeps that run's lock until its ending is saved.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task AConnectorsOwnThrottleAfterACreateLandedIsUnknown()
    {
        // The create lands, then the connector reports Throttled (its target was busy on a second call).
        var (item, name, mock, _) = await PlannedNewAccountAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var busy = new FaultAfterWriteConnector(mock, ActionCatalog.AccountCreate, ResultCodes.Throttled);
        (await RunAsync(item, new ConnectorTarget(busy), clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Operations.Single(o => o.Step == 1).Unsettled.ShouldBeTrue("a connector's throttle may come after its write");

        // Run again: the account is there, and whose it is can't be told. A person reconciles it; it is never "someone else's".
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, new ConnectorTarget(mock), clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(
            i => i.State.ShouldBe(ProvisioningState.Failed),
            i => i.Problem.ShouldBe(ProvisioningRunner.OutcomeUnknown));
        (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN={name},{AccountContainer}"), Ct)).ShouldNotBeNull("left for a person");
    }

    [Fact]
    public async Task ASuccessIsNeverSavedOverAGrantLockAnotherRunTook()
    {
        // While the final read runs, this run's lock runs out and another run (an undo) takes it.
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        var other = Guid.CreateVersion7();
        var probe = new LockProbeTarget(async step =>
        {
            if (step.Action == ProvisioningPlanner.EntitlementAdd)
            {
                await using var context = ProvisioningContext();
                var row = await context.GrantLocks.SingleAsync(l => l.AccountId == planned.AccountId && l.EntitlementId == planned.EntitlementId, Ct);
                row.Hold(other, Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1));
                await context.SaveChangesAsync(Ct);
            }

            return true;
        });
        (await RunAsync(item, probe)).ShouldBeTrue();

        // The success's save conflicts on the lock: nothing is recorded, and the other run keeps its lock.
        (await PlannedAsync(item)).Item.State.ShouldNotBe(ProvisioningState.Succeeded);
        (await GrantLockAsync(planned)).ShouldNotBeNull().HeldBy.ShouldBe(other);
    }

    [Fact]
    public async Task WaitingForABusyGrantCountsNoAttempt()
    {
        // Another item holds the grant for the next hour.
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        await using (var context = ProvisioningContext())
        {
            context.GrantLocks.Add(GrantLock.Take(planned.AccountId!.Value, planned.EntitlementId!.Value, Guid.CreateVersion7(), Guid.CreateVersion7(), clock.GetUtcNow().AddHours(1)));
            await context.SaveChangesAsync(Ct);
        }

        var target = new ScriptedTarget(StepStatus.Done) { DoneCode = ProvisioningPlanner.Applied };
        (await RunAsync(item, target, clock, maxAttempts: 1)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(
            i => i.State.ShouldBe(ProvisioningState.Planned),
            i => i.Attempts.ShouldBe(0, "nothing was tried"),
            i => i.Postponements.ShouldBe(1));
        target.Runs.ShouldBe(0);

        // Once the other item's hold ends, it runs, even with a single attempt allowed.
        clock.Advance(TimeSpan.FromHours(2));
        (await RunAsync(item, target, clock, maxAttempts: 1)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Succeeded);
    }

    [Fact]
    public async Task AnUndoInsideARunKeepsItsLockUntilTheEndingIsSaved()
    {
        // The add applies but the final read doesn't match: the run undoes it, and its undo's read-back notes the lock.
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        GrantLock? seen = null;
        DateTimeOffset? seenAt = null;
        var probe = new LockProbeTarget(async step =>
        {
            if (step.Action != ProvisioningPlanner.EntitlementRemove)
            {
                return false;
            }

            seenAt = DateTimeOffset.UtcNow;
            seen = await GrantLockAsync(planned);
            return true;
        });
        (await RunAsync(item, probe)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.Problem.ShouldBe("VerifyMismatch");

        // After its removal, until the failure was saved, the run still held the lock; then it was released.
        seen.ShouldNotBeNull().HeldBy.ShouldBe(item);
        seen!.UntilUtc.ShouldBeGreaterThan(seenAt!.Value);
        (await GrantLockAsync(planned)).ShouldNotBeNull().UntilUtc.ShouldBeLessThan(DateTimeOffset.UtcNow, "released once saved");
    }

    private async Task<GrantLock?> GrantLockAsync(ProvisioningItem planned)
    {
        await using var context = ProvisioningContext();
        return await context.GrantLocks.AsNoTracking().SingleOrDefaultAsync(l => l.AccountId == planned.AccountId && l.EntitlementId == planned.EntitlementId, Ct);
    }

    /// <summary>Every step applies; each read of the target is answered by a callback.</summary>
    private sealed class LockProbeTarget(Func<TargetStep, Task<bool?>> read) : IProvisioningTarget
    {
        public Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken) =>
            Task.FromResult(new StepResult(StepStatus.Done, ProvisioningPlanner.Applied));

        public Task<bool?> VerifyAsync(TargetStep operation, CancellationToken cancellationToken) => read(operation);

        public Task<bool?> ExistsAsync(string objectType, string id, CancellationToken cancellationToken) => Task.FromResult<bool?>(true);
    }
}
