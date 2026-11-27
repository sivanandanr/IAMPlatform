using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 9 slice 11 (DL-106), the DL-105 re-review's findings, on both engines: a damaged stored record of a step is audited
/// as such, not as a failed call; and under a clock that doesn't move, a lock taken by another run still stops this run before
/// its next call and before its undo removes anything.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task ADamagedLedgerRecordIsAuditedAsSuchAndItsOutcomeIsUnknown()
    {
        var item = await PlannedExistingAsync();
        var damaged = new StepProbeTarget(onRun: _ => throw new InvalidDataException("A ledger record has a status the SDK never records."));
        (await RunAsync(item, damaged)).ShouldBeTrue();

        // Unknown, not "not done": the step is unsettled and goes on waiting; the audit says the record was damaged.
        (await PlannedAsync(item)).Operations.Single().ShouldSatisfyAllConditions(
            o => o.State.ShouldBe(ProvisioningState.Planned),
            o => o.Unsettled.ShouldBeTrue());
        await using var audit = TestSupport.CreateAudit(db.Provider, db.AuditAppConnectionString);
        (await new AuditTrail(audit).ForTargetAsync(ProvisioningAuditActions.Target, item.ToString(), 100, Ct))
            .ShouldContain(e => e.Action == ProvisioningAuditActions.StepRetry && e.Payload["code"] == ProvisioningRunner.LedgerDamaged);
    }

    [Fact]
    public async Task UnderAStoppedClockALockTakenDuringAStepStopsTheRunBeforeItsNextCall()
    {
        // The runner's clock doesn't move. While the add runs, another run takes the lock: renewing it before the final read
        // still checks its version, so the run stops there and never reads the target or records anything.
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var probe = new StepProbeTarget(onRun: async step =>
        {
            await TakeOverLockAsync(planned, clock.GetUtcNow().AddMinutes(1));
            return new StepResult(StepStatus.Done, ProvisioningPlanner.Applied);
        });
        (await RunAsync(item, probe, clock)).ShouldBeTrue();
        probe.Reads.ShouldBe(0, "the final read never ran");
        (await PlannedAsync(item)).Item.State.ShouldNotBe(ProvisioningState.Succeeded);
    }

    [Fact]
    public async Task UnderAStoppedClockAnUndoWaitsWhenAnotherRunTookItsLock()
    {
        // The add applies; while the final read runs (it doesn't match), another run takes the lock. The undo inside this run
        // takes the lock again: its version is checked though nothing in the row changed, so the undo waits; nothing is removed.
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var probe = new StepProbeTarget(onRead: async step =>
        {
            if (step.Action == ProvisioningPlanner.EntitlementAdd)
            {
                await TakeOverLockAsync(planned, clock.GetUtcNow().AddMinutes(1));
            }

            return false;
        });
        (await RunAsync(item, probe, clock)).ShouldBeTrue();
        probe.Removes.ShouldBe(0, "nothing was removed under another run's lock");
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(
            i => i.State.ShouldBe(ProvisioningState.Planned, "the undo waits for the lock"),
            i => i.UndoAttempts.ShouldBe(1));
    }

    /// <summary>A target whose writes and reads are answered by callbacks (by default: applied, and matching); counts them.</summary>
    private sealed class StepProbeTarget(Func<TargetStep, Task<StepResult>>? onRun = null, Func<TargetStep, Task<bool?>>? onRead = null) : IProvisioningTarget
    {
        public int Reads { get; private set; }

        public int Removes { get; private set; }

        public Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken)
        {
            if (operation.Action == ProvisioningPlanner.EntitlementRemove)
            {
                Removes++;
            }

            return onRun?.Invoke(operation) ?? Task.FromResult(new StepResult(StepStatus.Done, ProvisioningPlanner.Applied));
        }

        public Task<bool?> VerifyAsync(TargetStep operation, CancellationToken cancellationToken)
        {
            Reads++;
            return onRead?.Invoke(operation) ?? Task.FromResult<bool?>(true);
        }

        public Task<bool?> ExistsAsync(string objectType, string id, CancellationToken cancellationToken) => Task.FromResult<bool?>(true);
    }
}
