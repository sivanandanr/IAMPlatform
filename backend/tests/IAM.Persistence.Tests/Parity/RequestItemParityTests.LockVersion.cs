using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 9 slice 10 (DL-105), the DL-104 re-review's findings, on both engines: the success's save always checks the grant
/// lock's version, even when the clock hasn't moved; a lock taken and released meanwhile still stops it; the item's next run
/// fails closed; and a busy grant past its postponements counts attempts and ends for a person.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task ALostSuccessUnderAStoppedClockIsNeverRecordedAndTheNextRunFailsClosed()
    {
        // The runner's clock doesn't move, so renewing the lock before the success changes nothing in its row. Meanwhile, during
        // the final read, another run takes the lock (until a minute from the runner's now).
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var probe = new LockProbeTarget(async step =>
        {
            if (step.Action == ProvisioningPlanner.EntitlementAdd)
            {
                await TakeOverLockAsync(planned, clock.GetUtcNow().AddMinutes(1));
            }

            return true;
        });
        (await RunAsync(item, probe, clock)).ShouldBeTrue();

        // The save conflicted on the lock's version: no success, in the item or the audit.
        (await PlannedAsync(item)).Item.State.ShouldNotBe(ProvisioningState.Succeeded);
        (await AuditCountAsync(item.ToString(), ProvisioningAuditActions.Succeeded, "Succeeded")).ShouldBe(0);

        // The other run removed the access. The item's next run (its lease and the other lock run out) reads the target, finds
        // it doesn't match, and fails for a person after undoing what it did.
        clock.Advance(TimeSpan.FromHours(2));
        var removed = new ScriptedTarget(StepStatus.Done) { DoneCode = ProvisioningPlanner.Applied, MatchesStep = s => s.Action == ProvisioningPlanner.EntitlementRemove };
        (await RunAsync(item, removed, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(
            i => i.State.ShouldBe(ProvisioningState.Failed),
            i => i.Problem.ShouldBe("VerifyMismatch"));
        (await AuditCountAsync(item.ToString(), ProvisioningAuditActions.Succeeded, "Succeeded")).ShouldBe(0);
    }

    [Fact]
    public async Task ALockTakenAndReleasedDuringTheFinalReadStillStopsTheSuccess()
    {
        // Another run takes the lock and releases it again before this run saves: the row looks free, but its version moved.
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        var probe = new LockProbeTarget(async step =>
        {
            if (step.Action == ProvisioningPlanner.EntitlementAdd)
            {
                await TakeOverLockAsync(planned, DateTimeOffset.UtcNow.AddHours(1));
                await TakeOverLockAsync(planned, DateTimeOffset.UtcNow.AddSeconds(-1));
            }

            return true;
        });
        (await RunAsync(item, probe)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldNotBe(ProvisioningState.Succeeded);
        (await AuditCountAsync(item.ToString(), ProvisioningAuditActions.Succeeded, "Succeeded")).ShouldBe(0);
    }

    [Fact]
    public async Task ABusyGrantPastItsPostponementsCountsAttemptsAndEndsForAPerson()
    {
        // Another item holds the grant for ten hours; one postponement and one attempt are allowed.
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        await using (var context = ProvisioningContext())
        {
            context.GrantLocks.Add(GrantLock.Take(planned.AccountId!.Value, planned.EntitlementId!.Value, Guid.CreateVersion7(), Guid.CreateVersion7(), clock.GetUtcNow().AddHours(10)));
            await context.SaveChangesAsync(Ct);
        }

        var target = new ScriptedTarget(StepStatus.Done) { DoneCode = ProvisioningPlanner.Applied };
        (await RunAsync(item, target, clock, maxAttempts: 1, maxPostponements: 1)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Planned), i => i.Postponements.ShouldBe(1));

        // Still busy, and the postponement is used up: it counts an attempt, the last one, and the item ends for a person.
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, target, clock, maxAttempts: 1, maxPostponements: 1)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(
            i => i.State.ShouldBe(ProvisioningState.Failed),
            i => i.Problem.ShouldBe("RetriesExhausted"));
        target.Runs.ShouldBe(0, "nothing was ever sent to the target");
    }

    /// <summary>Another run holds the item's grant lock until <paramref name="until"/> (a time past releases it).</summary>
    private async Task TakeOverLockAsync(ProvisioningItem planned, DateTimeOffset until)
    {
        await using var context = ProvisioningContext();
        var row = await context.GrantLocks.SingleAsync(l => l.AccountId == planned.AccountId && l.EntitlementId == planned.EntitlementId, Ct);
        row.Hold(Guid.CreateVersion7(), Guid.CreateVersion7(), until);
        await context.SaveChangesAsync(Ct);
    }
}
