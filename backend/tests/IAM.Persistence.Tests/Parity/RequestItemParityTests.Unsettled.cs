using IAM.Connectors.Sdk;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Modules.Request.Application.Items;
using IAM.Workers.Provisioning;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 9 slice 7 (DL-102), the DL-101 re-review's findings, on both engines: an unknown outcome is kept across attempts and
/// honoured by every way an item ends; an unsettled create that later reads "already there" goes to a person instead of being
/// called someone else's; the mark can't be cleared; an undo waits for another item only until its last pass; a lock held by
/// an older run of the same item isn't taken; and no login writes PostgreSQL's name sequence.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task AnUnknownOutcomeIsKeptWhenTheItemEndsOnALaterRun()
    {
        // The add's call breaks off on its first attempt: it waits to run again, marked unsettled.
        var item = await PlannedExistingAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var broken = new ScriptedTarget(StepStatus.Unknown);
        (await RunAsync(item, broken, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Operations.Single().ShouldSatisfyAllConditions(
            o => o.State.ShouldBe(ProvisioningState.Planned),
            o => o.Unsettled.ShouldBeTrue());

        // The next run stops before the add runs again (the account isn't on the target any more): nothing is removed, and the
        // item says the outcome is unknown, for a person.
        clock.Advance(TimeSpan.FromHours(1));
        var next = new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied", Exists = false };
        (await RunAsync(item, next, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(
            i => i.State.ShouldBe(ProvisioningState.Failed),
            i => i.Problem.ShouldBe(ProvisioningRunner.OutcomeUnknown));
        next.Runs.ShouldBe(0);

        // Without its connector the same holds.
        var orphan = await PlannedExistingAsync();
        (await RunAsync(orphan, new ScriptedTarget(StepStatus.Unknown), clock)).ShouldBeTrue();
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(orphan, null, clock)).ShouldBeTrue();
        (await PlannedAsync(orphan)).Item.Problem.ShouldBe(ProvisioningRunner.OutcomeUnknown);
    }

    [Fact]
    public async Task AnUnsettledCreateThatFindsItsObjectGoesToAPerson()
    {
        // The create's write lands, but the connector faults before it can say so and its ledger keeps nothing.
        var (item, name, mock, _) = await PlannedNewAccountAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        (await RunAsync(item, new LostAnswerTarget(new ConnectorTarget(mock), ProvisioningPlanner.AccountCreate), clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Operations.Single(o => o.Step == 1).Unsettled.ShouldBeTrue();

        // Run again, the create finds the object: that may be its own, so it is never called someone else's (TargetExists),
        // and nothing is called on it.
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, new ConnectorTarget(mock), clock)).ShouldBeTrue();
        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe(ProvisioningRunner.OutcomeUnknown));
        steps.Single(s => s.Step == 1).ResultCode.ShouldBe(ProvisioningRunner.OutcomeUnknown);
        steps.ShouldNotContain(s => s.Step > 10);
        (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN={name},{AccountContainer}"), Ct)).ShouldNotBeNull("left for a person");
    }

    [Fact]
    public async Task AnUnsettledAddFoundInPlaceIsNeverTakenAsThisItemsToRemove()
    {
        // The add's call breaks off; run again, the connector says the access is already there, but the final read fails.
        var item = await PlannedExistingAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var target = new ScriptedTarget(StepStatus.Unknown, StepStatus.Done) { DoneCode = "AlreadyInState", Matches = false };
        (await RunAsync(item, target, clock)).ShouldBeTrue();
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, target, clock)).ShouldBeTrue();

        // Whether this item put it there is unknown: nothing is removed, and a person reconciles it.
        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe(ProvisioningRunner.OutcomeUnknown));
        steps.Single().ResultCode.ShouldBe(ProvisioningRunner.OutcomeUnknown);
        steps.ShouldNotContain(s => s.Step > 10);
        target.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task AnUnknownOutcomeCantBeCleared()
    {
        var item = await PlannedExistingAsync();
        (await RunAsync(item, new ScriptedTarget(StepStatus.Unknown))).ShouldBeTrue();
        var table = IsSqlServer ? "[provisioning].[ProvisioningOperation]" : "provisioning.provisioning_operation";
        var refused = await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.ProvisioningAppConnectionString,
            $"UPDATE {table} SET {Col("Unsettled")} = {(IsSqlServer ? "0" : "false")} WHERE {Col("ItemId")} = '{item}'", Ct));
        refused.Message.ShouldContain("DL-102");
    }

    [Fact]
    public async Task AnUndoWaitsForAnotherItemOnlyUntilItsLastPass()
    {
        // The other item stays planned for ever (a slow item, or one a forger holds back).
        var item = await PlannedExistingAsync();
        await HolderAsync(item, succeeded: false);
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var target = new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied", MatchesStep = s => s.Action == ProvisioningPlanner.EntitlementRemove };
        (await RunAsync(item, target, clock, undoMaxAttempts: 2)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Planned, "the first pass waits");

        // On its last pass the undo removes the access rather than end UndoFailed with it left.
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, target, clock, undoMaxAttempts: 2)).ShouldBeTrue();
        var (failed, steps) = await PlannedAsync(item);
        failed.Problem.ShouldBe("VerifyMismatch");
        steps.Single(s => s.Step == 11).ResultCode.ShouldBe("Applied");
        target.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task AGrantLockAnOlderRunOfTheSameItemHoldsIsNotTaken()
    {
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        await using (var context = ProvisioningContext())
        {
            context.GrantLocks.Add(GrantLock.Take(planned.AccountId!.Value, planned.EntitlementId!.Value, item, Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1)));
            await context.SaveChangesAsync(Ct);
        }

        var target = new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied" };
        (await RunAsync(item, target)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Planned, "another run of the same item holds it");
        target.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task NoLoginWritesTheNameSequenceOnPostgreSql()
    {
        // On SQL Server an identity refuses a written value without IDENTITY_INSERT (ANameSequenceIsTheDatabasesAlone).
        Assert.SkipWhen(IsSqlServer, "PostgreSQL only: SQL Server's identity already refuses a written seq");

        var world = await WorldAsync();
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);
        var name = $"adm.{Guid.NewGuid():N}"[..18];
        Guid stage;
        var (workflow, workflowContext) = Workflow(world.EntitlementOwner);
        await using (workflowContext)
        {
            stage = (await workflow.FindAsync(item, Ct)).ShouldNotBeNull().Stages.Single(s => s is { Kind: "Action", State: "Active" }).StageId;
        }

        await using (var scope = Service(world.EntitlementOwner, route))
        {
            await scope.Service.GiveAccountNameAsync(item, stage, name, Ct);
        }

        // The request login writes a row with seq 1 by OVERRIDING SYSTEM VALUE: the database takes the next value instead.
        var forged = $"adm.{Guid.NewGuid():N}"[..18];
        await db.ExecuteAsync(db.RequestAppConnectionString,
            "INSERT INTO request.account_name_assignment (id, seq, item_id, stage_id, previous_name, name, set_by, set_at_utc, created_at_utc, created_by, modified_at_utc, modified_by, version) "
            + $"OVERRIDING SYSTEM VALUE SELECT gen_random_uuid(), 1, item_id, stage_id, name, '{forged}', set_by, set_at_utc, created_at_utc, created_by, modified_at_utc, modified_by, version "
            + $"FROM request.account_name_assignment WHERE item_id = '{item}'", Ct);
        (await ItemSource().CurrentAccountNameAsync(item, Ct)).ShouldNotBeNull().Name.ShouldBe(forged, "numbered after the genuine one, not at 1");
    }

    /// <summary>A target whose first call for one action lands under another id, then faults: the connector's ledger keeps nothing.</summary>
    private sealed class LostAnswerTarget(IProvisioningTarget inner, string action) : IProvisioningTarget
    {
        private bool _lost;

        public async Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken)
        {
            if (operation.Action != action || _lost)
            {
                return await inner.RunAsync(operation, cancellationToken);
            }

            _lost = true;
            await inner.RunAsync(operation with { OperationId = Guid.CreateVersion7() }, cancellationToken);
            return new StepResult(StepStatus.Unknown, "ConnectorError");
        }

        public Task<bool?> VerifyAsync(TargetStep operation, CancellationToken cancellationToken) => inner.VerifyAsync(operation, cancellationToken);

        public Task<bool?> ExistsAsync(string objectType, string id, CancellationToken cancellationToken) => inner.ExistsAsync(objectType, id, cancellationToken);
    }
}
