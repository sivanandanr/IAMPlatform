using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Modules.Request.Application.Items;
using IAM.Modules.Workflow.Contracts;
using IAM.Modules.Workflow.Domain.Approvals;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 9 slice 5 (DL-100), the DL-099 re-review's findings, on both engines: an undo changes nothing it can't trust (an
/// audited step and the application's own scope); access another item holds is kept; an undo that fails for now is retried;
/// an approval that recorded no name never provisions the proposal; a completion of a name the person didn't see is
/// refused; and postponing for a changing approval has a limit.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task AStepWithNoAuditedRecordIsNeverUndone()
    {
        // The provisioning login marks the planned add as done and Applied, as if it had run, without the target being called.
        var item = await PlannedExistingAsync();
        var table = IsSqlServer ? "[provisioning].[ProvisioningOperation]" : "provisioning.provisioning_operation";
        await db.ExecuteAsync(db.ProvisioningAppConnectionString, $"UPDATE {table} SET {Col("State")} = 'Running' WHERE {Col("ItemId")} = '{item}'", Ct);
        await db.ExecuteAsync(db.ProvisioningAppConnectionString,
            $"UPDATE {table} SET {Col("State")} = 'Succeeded', {Col("ResultCode")} = 'Applied', {Col("CompletedAtUtc")} = {(IsSqlServer ? "SYSUTCDATETIME()" : "now()")} WHERE {Col("ItemId")} = '{item}'", Ct);

        // The run reads the target, which doesn't match, and fails the item; the undo finds no audited step and calls nothing.
        var target = new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied", Matches = false };
        (await RunAsync(item, target)).ShouldBeTrue();
        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("UndoFailed"), i => i.UndoReason.ShouldBe("VerifyMismatch"));
        steps.Where(s => s.Step > 10).Count().ShouldBe(1, "the removal is stored");
        steps.Where(s => s.Step > 10).ShouldAllBe(s => s.State == ProvisioningState.Planned, "stored, never run");
        target.Runs.ShouldBe(0, "nothing was called on the target");
    }

    [Fact]
    public async Task AnUndoOutsideTheApplicationsScopeCallsNothing()
    {
        // The add ran and was audited; the final read can't be made, so the item waits.
        var item = await PlannedExistingAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var target = new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied", MatchesStep = _ => null };
        (await RunAsync(item, target, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Planned);

        // The application's group container is now another one: the re-check stops, and the group is out of scope for an undo.
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, target, clock, groupContainer: "OU=Other Groups,DC=corp,DC=test")).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("UndoFailed"));
        target.Runs.ShouldBe(1, "only the add ran");

        // The same steps and audit in the application's own scope are undone (DL-101): only the scope refused the one above.
        var control = await PlannedExistingAsync();
        var controlTarget = new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied", MatchesStep = s => s.Action == ProvisioningPlanner.EntitlementRemove };
        (await RunAsync(control, controlTarget, clock)).ShouldBeTrue();
        (await PlannedAsync(control)).Item.Problem.ShouldBe("VerifyMismatch");
        controlTarget.Runs.ShouldBe(2, "the add, then its removal");
    }

    [Fact]
    public async Task AccessAnotherItemHoldsIsKept()
    {
        // Another item already granted the same entitlement on the same account and succeeded. (One waiting request per person
        // and entitlement means this can't come from two requests today; the check is for when requests can be made again.)
        var second = await PlannedExistingAsync();
        await HolderAsync(second, succeeded: true);

        // This item's run fails after its add: its undo keeps what the other item holds.
        var target = new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied", MatchesStep = s => s.Action == ProvisioningPlanner.EntitlementRemove };
        (await RunAsync(second, target)).ShouldBeTrue();
        var (failed, steps) = await PlannedAsync(second);
        failed.Problem.ShouldBe("VerifyMismatch");
        steps.Single(s => s.Step == 11).ResultCode.ShouldBe(ProvisioningRunner.KeptForOtherItem);
        target.Runs.ShouldBe(1, "the removal was never called");
    }

    [Fact]
    public async Task AnUndoThatFailsForNowIsTriedAgain()
    {
        var item = await PlannedExistingAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var target = new ScriptedTarget(StepStatus.Done, StepStatus.Retry, StepStatus.Done)
        {
            DoneCode = "Applied",
            MatchesStep = s => s.Action == ProvisioningPlanner.EntitlementRemove,
        };

        // The add runs; the read doesn't match; the removal is throttled: the item waits, still undoing.
        (await RunAsync(item, target, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).ShouldSatisfyAllConditions(
            p => p.Item.State.ShouldBe(ProvisioningState.Planned),
            p => p.Item.UndoReason.ShouldBe("VerifyMismatch"),
            p => p.Operations.Single(o => o.Step == 11).State.ShouldBe(ProvisioningState.Planned));

        // The next run finishes the undo first and fails with the original reason.
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, target, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).ShouldSatisfyAllConditions(
            p => p.Item.State.ShouldBe(ProvisioningState.Failed),
            p => p.Item.Problem.ShouldBe("VerifyMismatch"),
            p => p.Operations.Single(o => o.Step == 11).State.ShouldBe(ProvisioningState.Succeeded));
        target.Runs.ShouldBe(3);

        // An undo that never gets through stops at its own limit.
        var stuck = await PlannedExistingAsync();
        var throttled = new ScriptedTarget(StepStatus.Done, StepStatus.Retry) { DoneCode = "Applied", MatchesStep = s => s.Action == ProvisioningPlanner.EntitlementRemove };
        for (var pass = 0; pass < 3; pass++)
        {
            (await RunAsync(stuck, throttled, clock, undoMaxAttempts: 2)).ShouldBeTrue();
            clock.Advance(TimeSpan.FromHours(2));
        }

        (await PlannedAsync(stuck)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("UndoFailed"), i => i.UndoAttempts.ShouldBe(2));
    }

    [Fact]
    public async Task AnApprovalThatRecordedNoNameNeverProvisionsTheProposal()
    {
        // A name level completed before DL-099 recorded no name: the item is blocked, never given the naming rule's proposal.
        var world = await WorldAsync();
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);
        var (workflow, context) = Workflow(world.EntitlementOwner, new NoItemSource());
        await using (context)
        {
            var instance = (await workflow.FindAsync(item, Ct)).ShouldNotBeNull();
            var stage = instance.Stages.Single(s => s is { Kind: "Action", State: "Active" });
            await using (var scope = Service(world.EntitlementOwner, route))
            {
                await scope.Service.GiveAccountNameAsync(item, stage.StageId, $"adm.{Guid.NewGuid():N}"[..18], Ct);
            }

            await workflow.CompleteAsync(item, instance.Version, Guid.CreateVersion7(), stage.StageId, "Done", Ct);
        }

        (await IntegrityAsync(item)).AccountName.ShouldBeNull();
        (await HandleAsync(world, await WorkflowEnvelopeAsync(item, ApprovalClosed.MessageType))).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(item)).Item.Problem.ShouldBe("NameNotApproved");
    }

    [Fact]
    public async Task ACompletionOfANameThePersonDidNotSeeIsRefused()
    {
        var world = await WorldAsync();
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);
        var name = $"adm.{Guid.NewGuid():N}"[..18];
        var (workflow, context) = Workflow(world.EntitlementOwner);
        await using (context)
        {
            var instance = (await workflow.FindAsync(item, Ct)).ShouldNotBeNull();
            var stage = instance.Stages.Single(s => s is { Kind: "Action", State: "Active" });
            await using (var scope = Service(world.EntitlementOwner, route))
            {
                await scope.Service.GiveAccountNameAsync(item, stage.StageId, name, Ct);
            }

            (await Should.ThrowAsync<ApprovalRefusedException>(() => workflow.CompleteAsync(item, instance.Version, Guid.CreateVersion7(), stage.StageId, "Done", Ct, expectedAccountName: "adm.someone.else")))
                .Refusal.ShouldBe(ApprovalRefusal.AccountNameChanged);
            (await workflow.CompleteAsync(item, instance.Version, Guid.CreateVersion7(), stage.StageId, "Done", Ct, expectedAccountName: name)).State.ShouldBe("Approved");
        }
    }

    [Fact]
    public async Task PostponingForAChangingApprovalHasALimit()
    {
        var item = await PlannedExistingAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        (await RunAsync(item, new ScriptedTarget(StepStatus.Done), clock, integrity: new ChangingIntegrity(), maxPostponements: 1)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.Attempts.ShouldBe(0), i => i.Postponements.ShouldBe(1));

        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, new ScriptedTarget(StepStatus.Done), clock, integrity: new ChangingIntegrity(), maxPostponements: 1)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.Attempts.ShouldBe(1), i => i.Postponements.ShouldBe(1));
    }

    [Fact]
    public async Task AChangingApprovalDefersTheEventInsteadOfFailingIt()
    {
        var world = await WorldAsync();
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null));
        const string actor = "system:provisioning";
        await using var context = TestSupport.CreateProvisioning(db.Provider, db.ProvisioningAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));
        var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor(actor),
            new AuditActionCatalog(ProvisioningAuditActions.All.Select(a => new AuditActionRegistration(a))));
        var (decider, contexts) = Decider(Bound(world), integrity: new ChangingIntegrity());
        try
        {
            var handler = new AuthorizedApprovals(context, TestSupport.ProviderFor(db.Provider), TimeProvider.System, writer, decider);
            await Should.ThrowAsync<MessageDeferredException>(() => handler.HandleAsync(Authorized(item, 1), Ct));
        }
        finally
        {
            foreach (var c in contexts)
            {
                await c.DisposeAsync();
            }
        }
    }
}
