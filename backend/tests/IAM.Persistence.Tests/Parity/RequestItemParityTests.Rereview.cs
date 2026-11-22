using IAM.BuildingBlocks.Messaging;
using IAM.Connectors.Sdk;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Modules.Request.Application.Items;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Workflow.Contracts;
using IAM.Workers.Provisioning;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 9 slice 4 (DL-099), the stage 9 re-review's findings, on both engines: the name an Action level stood behind is
/// recorded with its completion and is the one provisioned; every failure after a step ran undoes it, and an undo that
/// stopped half-way is finished first; a target that can't be read and an approval still being written are tried again; an
/// early or forged event stores nothing; the item carries the request's correlation id; one directory object is one account.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task TheNameALevelStoodBehindIsRecordedWithItsCompletion()
    {
        var world = await WorldAsync();
        var name = $"adm.{Guid.NewGuid():N}"[..18];
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);
        await NameAndCompleteAsync(world, item, route, name);

        var recorded = (await IntegrityAsync(item)).AccountName.ShouldNotBeNull();
        recorded.NameHash.ShouldBe(AccountNameHash.Of(name));
        recorded.AssignmentId.ShouldNotBeNull();

        // The audit of the completion says the same, by assignment and hash, never the name itself.
        await using var audit = TestSupport.CreateAudit(db.Provider, db.AuditAppConnectionString);
        var completed = (await new AuditTrail(audit).ForTargetAsync("workflow.approval", item.ToString("D"), 100, Ct))
            .Single(e => e.Action == "workflow.approval.completed");
        completed.Payload["nameHash"].ShouldBe(recorded.NameHash);
        completed.Payload["nameAssignmentId"].ShouldBe(recorded.AssignmentId.Value.ToString("D"));
        completed.Payload.Values.ShouldNotContain(name);

        (await HandleAsync(world, await WorkflowEnvelopeAsync(item, ApprovalClosed.MessageType))).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(item)).Item.AccountName.ShouldBe(name);
    }

    [Fact]
    public async Task AnEntitlementTheRunAddedIsTakenAwayWhenTheTargetDoesNotMatch()
    {
        // The add was this item's (Applied), but the final read doesn't match: the entitlement is removed before a person looks.
        var item = await PlannedExistingAsync();
        var target = new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied", MatchesStep = s => s.Action == ProvisioningPlanner.EntitlementRemove };
        (await RunAsync(item, target)).ShouldBeTrue();
        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("VerifyMismatch"));
        steps.Where(s => s.Step > 10).Select(s => (s.Action, s.State)).ShouldBe([(ProvisioningPlanner.EntitlementRemove, ProvisioningState.Succeeded)]);
        (await AuditCountAsync(item.ToString(), ProvisioningAuditActions.StepUndone, "Succeeded")).ShouldBe(1);

        // Access the person already had is never taken away.
        var had = await PlannedExistingAsync();
        (await RunAsync(had, new ScriptedTarget(StepStatus.Done) { DoneCode = "AlreadyInState", Matches = false })).ShouldBeTrue();
        (await PlannedAsync(had)).ShouldSatisfyAllConditions(
            p => p.Item.Problem.ShouldBe("VerifyMismatch"),
            p => p.Operations.ShouldNotContain(o => o.Step > 10));

        // An undo that the target doesn't confirm leaves the item for a person as UndoFailed.
        var stuck = await PlannedExistingAsync();
        (await RunAsync(stuck, new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied", Matches = false })).ShouldBeTrue();
        (await PlannedAsync(stuck)).Item.Problem.ShouldBe("UndoFailed");
    }

    [Fact]
    public async Task AStepRejectedAfterTheAccountWasCreatedUndoesIt()
    {
        var (item, name, mock, group) = await PlannedNewAccountAsync();
        var target = new RejectingTarget(new ConnectorTarget(mock), ProvisioningPlanner.AccountEnable);
        (await RunAsync(item, target)).ShouldBeTrue();

        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("Refused"));
        steps.Where(s => s.Step > 10).ShouldAllBe(s => s.State == ProvisioningState.Succeeded);
        var account = (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN={name},{AccountContainer}"), Ct)).ShouldNotBeNull();
        account.Enabled.ShouldBe(false);
        account.Links.ShouldNotContain(l => string.Equals(l.Id, group, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnUndoThatStoppedHalfWayIsFinishedFirst()
    {
        var (item, name, mock, group) = await PlannedNewAccountAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        using (var stop = new CancellationTokenSource())
        {
            // The enable is refused; the undo removes the entitlement, then the runner stops as it disables the account.
            var stopping = new HookedTarget(new RejectingTarget(new ConnectorTarget(mock), ProvisioningPlanner.AccountEnable), step =>
            {
                if (step.Action == ProvisioningPlanner.AccountDisable)
                {
                    stop.Cancel();
                    stop.Token.ThrowIfCancellationRequested();
                }

                return Task.CompletedTask;
            });
            await Should.ThrowAsync<OperationCanceledException>(() => RunAsync(item, stopping, clock, cancellationToken: stop.Token));
        }

        var (running, half) = await PlannedAsync(item);
        running.State.ShouldBe(ProvisioningState.Running);
        half.Where(s => s.Step > 10).Select(s => s.State).ShouldBe([ProvisioningState.Succeeded, ProvisioningState.Running]);

        // The next runner finishes the undo before anything else, and never runs the plan again.
        clock.Advance(TimeSpan.FromMinutes(10));
        var next = new ConnectorTarget(mock);
        (await RunAsync(item, next, clock)).ShouldBeTrue();
        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("Interrupted"));
        steps.Where(s => s.Step > 10).ShouldAllBe(s => s.State == ProvisioningState.Succeeded);
        steps.Single(s => s.Action == ProvisioningPlanner.AccountEnable).State.ShouldBe(ProvisioningState.Failed);
        var account = (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN={name},{AccountContainer}"), Ct)).ShouldNotBeNull();
        account.Enabled.ShouldBe(false);
        account.Links.ShouldNotContain(l => string.Equals(l.Id, group, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ATargetThatCanNotBeReadIsTriedAgainNotFailed()
    {
        // The existing account's check can't read the directory: nothing is granted, and the item waits.
        var item = await PlannedExistingAsync();
        var unreadable = new ScriptedTarget(StepStatus.Done) { ReadsFail = true };
        (await RunAsync(item, unreadable)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Planned), i => i.Problem.ShouldBeNull(), i => i.Attempts.ShouldBe(1));
        unreadable.Runs.ShouldBe(0);

        // The final read fails after the step ran: tried again later, and the step isn't run twice.
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var late = await PlannedExistingAsync();
        var target = new ScriptedTarget(StepStatus.Done) { Exists = true };
        var reads = 0;
        var flaky = new HookedTarget(target, _ => Task.CompletedTask) { VerifyHook = () => ++reads == 1 ? throw new TimeoutException("The read broke off.") : true };
        (await RunAsync(late, flaky, clock)).ShouldBeTrue();
        (await PlannedAsync(late)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Planned), i => i.Problem.ShouldBeNull());
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(late, flaky, clock)).ShouldBeTrue();
        (await PlannedAsync(late)).Item.State.ShouldBe(ProvisioningState.Succeeded);
        target.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task AnApprovalStillBeingWrittenPostponesTheRunWithoutUsingAnAttempt()
    {
        var item = await PlannedExistingAsync();
        var target = new ScriptedTarget(StepStatus.Done);
        (await RunAsync(item, target, maxAttempts: 1, integrity: new ChangingIntegrity())).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(
            i => i.State.ShouldBe(ProvisioningState.Planned),
            i => i.Attempts.ShouldBe(0),
            i => i.NextAttemptAtUtc.ShouldNotBeNull());
        target.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task AnEarlyOrForgedEventCanNotUseUpTheGenuineOnesKey()
    {
        var world = await WorldAsync();
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null));
        var genuine = await WorkflowEnvelopeAsync(item, ApprovalAuthorized.MessageType);

        // A forged event under the genuine one's key, claiming more than the log holds: noted, and nothing is stored.
        var forged = MessageEnvelopes.Create(new ApprovalAuthorized(item, item, 99, 1), WorkflowEvents.Source, Guid.NewGuid().ToString("N"), null, genuine.IdempotencyKey, DateTimeOffset.UtcNow);
        (await HandleAsync(world, forged)).ShouldBe(MessageOutcome.Processed);
        await using (var context = TestSupport.CreateProvisioning(db.Provider, db.ProvisioningAppConnectionString))
        {
            (await context.Inbox.AsNoTracking().CountAsync(m => m.IdempotencyKey == genuine.IdempotencyKey, Ct)).ShouldBe(0);
        }

        (await HandleAsync(world, genuine)).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Planned);
    }

    [Fact]
    public async Task TheItemCarriesTheRequestsCorrelationIdNotTheEventsOwn()
    {
        var world = await WorldAsync();
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null));
        var requestCorrelation = await RequestCorrelationAsync(item);
        var genuine = await WorkflowEnvelopeAsync(item, ApprovalAuthorized.MessageType);
        var other = Guid.NewGuid().ToString("N");
        other.ShouldNotBe(requestCorrelation);

        (await HandleAsync(world, genuine with { CorrelationId = other })).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(item)).Item.CorrelationId.ShouldBe(requestCorrelation);
    }

    [Fact]
    public async Task OneDirectoryObjectIsOneAccountWhateverItsSpacingOrEscapes()
    {
        var world = await WorldAsync();
        var cn = $"Space {Guid.NewGuid():N}";
        await LinkAsync(world.Application, $"c{Guid.NewGuid():N}"[..12], $"CN={cn},OU=Users,DC=corp,DC=test");
        foreach (var lookalike in new[] { $"CN={cn}, OU=Users, DC=corp, DC=test", $"cn = {cn} ,ou=users,dc=corp,dc=test", $"CN={cn.Replace(" ", "\\20", StringComparison.Ordinal)},OU=Users,DC=corp,DC=test" })
        {
            await Should.ThrowAsync<Exception>(() => LinkAsync(world.Application, $"c{Guid.NewGuid():N}"[..12], lookalike), lookalike);
        }
    }

    // ---- Re-review helpers -------------------------------------------------------------------------------------------------

    private async Task<string> RequestCorrelationAsync(Guid item)
    {
        await using var request = TestSupport.CreateRequest(db.Provider, db.RequestAppConnectionString);
        return (await request.Items.AsNoTracking().Where(i => i.Id == item).Select(i => i.CorrelationId).SingleAsync(Ct)).ShouldNotBeNull();
    }

    /// <summary>A target that refuses one action, as a directory refusing a change.</summary>
    private sealed class RejectingTarget(IProvisioningTarget inner, string action) : IProvisioningTarget
    {
        public Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken) =>
            operation.Action == action ? Task.FromResult(new StepResult(StepStatus.Rejected, "Refused")) : inner.RunAsync(operation, cancellationToken);

        public Task<bool> VerifyAsync(TargetStep operation, CancellationToken cancellationToken) => inner.VerifyAsync(operation, cancellationToken);

        public Task<bool> ExistsAsync(string objectType, string id, CancellationToken cancellationToken) => inner.ExistsAsync(objectType, id, cancellationToken);
    }

    /// <summary>An approval that keeps changing while it is checked.</summary>
    private sealed class ChangingIntegrity : IApprovalIntegrity
    {
        public Task<ApprovalIntegrityReport> CheckAsync(Guid instanceId, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalIntegrityReport.Failed(instanceId, ApprovalIntegrityStatus.Changing, "Changing"));
    }
}
