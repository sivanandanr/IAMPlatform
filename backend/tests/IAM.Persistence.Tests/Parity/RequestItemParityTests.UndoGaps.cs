using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Persistence;
using IAM.Connectors.Sdk;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Modules.Request.Application.Items;
using IAM.Modules.Request.Infrastructure.Items;
using IAM.Modules.Workflow.Contracts;
using IAM.Modules.Workflow.Domain.Approvals;
using IAM.Workers.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 9 slice 6 (DL-101), the DL-100 re-review's findings, on both engines: an undo waits for another item still running on
/// the same access; a new account's undo runs whatever its add did; an interrupted add runs again under its operation id; an
/// unknown outcome is undone on a new account and goes to a person on an existing one; a forged step can't point an undo at a
/// name the approval doesn't stand behind or an object an account was recorded for; a completion must state the name, which
/// only an accepted person learns about; the submitted audit holds no name; and seq is the database's alone.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task AnUndoWaitsForAnotherItemOnTheSameAccess()
    {
        // Another item on the same account and group is planned but hasn't run its add.
        var item = await PlannedExistingAsync();
        var holder = await HolderAsync(item, succeeded: false);
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var target = new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied", MatchesStep = s => s.Action == ProvisioningPlanner.EntitlementRemove };

        // This item's add runs, the read doesn't match: the undo waits rather than keep access for an item that may never hold it.
        (await RunAsync(item, target, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).ShouldSatisfyAllConditions(
            p => p.Item.State.ShouldBe(ProvisioningState.Planned),
            p => p.Item.UndoReason.ShouldBe("VerifyMismatch"),
            p => p.Operations.Single(o => o.Step == 11).State.ShouldBe(ProvisioningState.Planned));
        target.Runs.ShouldBe(1, "only the add ran");

        // The other item fails with nothing of its own to undo: the access is removed after all.
        await using (var context = ProvisioningContext())
        {
            var other = await context.Items.SingleAsync(i => i.Id == holder, Ct);
            other.Claim(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            await context.SaveChangesAsync(Ct);
            other.Fail("Refused", DateTimeOffset.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, target, clock)).ShouldBeTrue();
        var (failed, steps) = await PlannedAsync(item);
        failed.Problem.ShouldBe("VerifyMismatch");
        steps.Single(s => s.Step == 11).ResultCode.ShouldBe("Applied", "removed, not kept");
        target.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task AnAddRejectedAfterTheCreateIsUndone()
    {
        var (item, name, mock, group) = await PlannedNewAccountAsync();
        (await RunAsync(item, new RejectingTarget(new ConnectorTarget(mock), ProvisioningPlanner.EntitlementAdd))).ShouldBeTrue();

        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("Refused"));
        steps.Where(s => s.Step > 10).Count().ShouldBe(2);
        steps.Where(s => s.Step > 10).ShouldAllBe(s => s.State == ProvisioningState.Succeeded);
        var account = (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN={name},{AccountContainer}"), Ct)).ShouldNotBeNull();
        account.Enabled.ShouldBe(false);
        account.Links.ShouldNotContain(l => string.Equals(l.Id, group, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnInterruptedAddRunsAgainUnderItsOperationId()
    {
        var (item, _, mock, _) = await PlannedNewAccountAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        using (var stop = new CancellationTokenSource())
        {
            // The add reaches the directory, then the runner stops before it saves the outcome.
            var stopping = new StoppingAfterTarget(new ConnectorTarget(mock), ProvisioningPlanner.EntitlementAdd, stop);
            await Should.ThrowAsync<OperationCanceledException>(() => RunAsync(item, stopping, clock, cancellationToken: stop.Token));
        }

        (await PlannedAsync(item)).Operations.Single(o => o.Step == 2).State.ShouldBe(ProvisioningState.Running);

        // The next runner runs it again under the same id: the connector's ledger answers Applied, not a read's AlreadyInState.
        clock.Advance(TimeSpan.FromMinutes(5));
        (await RunAsync(item, new ConnectorTarget(mock), clock)).ShouldBeTrue();
        var (done, steps) = await PlannedAsync(item);
        done.State.ShouldBe(ProvisioningState.Succeeded);
        steps.Single(s => s.Step == 2).ResultCode.ShouldBe(ProvisioningPlanner.Applied);
    }

    [Fact]
    public async Task AnUnknownOutcomeIsUndoneOnANewAccountAndGoesToAPersonOnAnExistingOne()
    {
        // A new account: the add's last call breaks off; everything on the account is this item's, so the undo runs.
        var (item, name, mock, group) = await PlannedNewAccountAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var breaking = new BreakingTarget(new ConnectorTarget(mock), ProvisioningPlanner.EntitlementAdd);
        for (var run = 0; run < 2; run++)
        {
            (await RunAsync(item, breaking, clock, maxAttempts: 2)).ShouldBeTrue();
            clock.Advance(TimeSpan.FromHours(1));
        }

        var (undone, steps) = await PlannedAsync(item);
        undone.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("RetriesExhausted"));
        steps.Single(s => s.Step == 2).ResultCode.ShouldBe(ProvisioningRunner.OutcomeUnknown);
        steps.Where(s => s.Step > 10).Count().ShouldBe(2);
        var account = (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN={name},{AccountContainer}"), Ct)).ShouldNotBeNull();
        account.Enabled.ShouldBe(false);
        account.Links.ShouldNotContain(l => string.Equals(l.Id, group, StringComparison.OrdinalIgnoreCase));

        // An existing account: the person may have had the access before, so nothing is removed and a person reconciles it.
        var existing = await PlannedExistingAsync();
        var unknown = new ScriptedTarget(StepStatus.Unknown);
        for (var run = 0; run < 2; run++)
        {
            (await RunAsync(existing, unknown, clock, maxAttempts: 2)).ShouldBeTrue();
            clock.Advance(TimeSpan.FromHours(1));
        }

        var (failed, existingSteps) = await PlannedAsync(existing);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe(ProvisioningRunner.OutcomeUnknown));
        existingSteps.ShouldNotContain(s => s.Step > 10, "no undo is stored or run");
        unknown.Runs.ShouldBe(2, "only the two attempts at the add");
    }

    [Fact]
    public async Task AForgedStepCantDisableANameTheApprovalDoesNotStandBehind()
    {
        // A genuine approval for one name; the provisioning login stores the item under another name, marks its create done
        // and forges the matching audit record.
        var world = await WorldAsync();
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);
        await NameAndCompleteAsync(world, item, route, $"adm.{Guid.NewGuid():N}"[..18]);
        await ForgeCreatedAsync(world, item, $"vic.{Guid.NewGuid():N}"[..18]);

        // The run sees the plan doesn't match; the undo calls nothing, though the forged record is intact.
        var target = new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied" };
        (await RunAsync(item, target)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.Problem.ShouldBe("UndoFailed"), i => i.UndoReason.ShouldBe("PlanMismatch"));
        target.Runs.ShouldBe(0, "nothing was disabled");
    }

    [Fact]
    public async Task AnUndoNeverDisablesAnObjectAnAccountWasRecordedFor()
    {
        // The create ran and was audited; the add failed for now, so the item waits.
        var (item, name, mock, _) = await PlannedNewAccountAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var waiting = new ScriptedTarget(StepStatus.Done, StepStatus.Retry) { DoneCode = "Applied" };
        (await RunAsync(item, waiting, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Planned);

        // Meanwhile two administrators link the directory object to an account: the re-check stops, and the undo leaves it be.
        var (application, _) = await WorldOfAsync(item);
        await LinkAsync(application, $"lnk.{Guid.NewGuid():N}"[..18], $"CN={name},{AccountContainer}");
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, waiting, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.Problem.ShouldBe("UndoFailed");
        waiting.Runs.ShouldBe(2, "the create and the add; no undo step");
        _ = mock;
    }

    [Fact]
    public async Task ACompletionMustStateTheNameAndOnlyAnAcceptedPersonLearnsAboutIt()
    {
        var world = await WorldAsync();
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);
        var name = $"adm.{Guid.NewGuid():N}"[..18];
        Guid stageId;
        long version;
        var (workflow, context) = Workflow(world.EntitlementOwner);
        await using (context)
        {
            var instance = (await workflow.FindAsync(item, Ct)).ShouldNotBeNull();
            stageId = instance.Stages.Single(s => s is { Kind: "Action", State: "Active" }).StageId;
            version = instance.Version;
            await using (var scope = Service(world.EntitlementOwner, route))
            {
                await scope.Service.GiveAccountNameAsync(item, stageId, name, Ct);
            }

            // Not stated: refused, whoever completes it.
            (await Should.ThrowAsync<ApprovalRefusedException>(() => workflow.CompleteAsync(item, version, Guid.CreateVersion7(), stageId, "Done", Ct)))
                .Refusal.ShouldBe(ApprovalRefusal.AccountNameNotConfirmed);
        }

        // Someone the level doesn't accept learns nothing about the name from a guess: the engine refuses them first.
        var (outsider, outsiderContext) = Workflow(world.Beneficiary);
        await using (outsiderContext)
        {
            var refusal = await Should.ThrowAsync<Exception>(() => outsider.CompleteAsync(item, version, Guid.CreateVersion7(), stageId, "Done", Ct, expectedAccountName: "adm.guess"));
            (refusal as ApprovalRefusedException)?.Refusal.ShouldNotBe(ApprovalRefusal.AccountNameChanged);
            (refusal as ApprovalRefusedException)?.Refusal.ShouldNotBe(ApprovalRefusal.AccountNameNotConfirmed);
        }

        // The submitted item's audit holds the proposal's hash, never a name field.
        await using var audit = TestSupport.CreateAudit(db.Provider, db.AuditAppConnectionString);
        var submitted = (await new AuditTrail(audit).ForTargetAsync(RequestAuditActions.Target, item.ToString("D"), 100, Ct))
            .Single(e => e.Action == RequestAuditActions.Submitted);
        submitted.Payload.ShouldContainKey("newAccountNameHash");
        submitted.Payload.ShouldNotContainKey("newAccountName");
    }

    [Fact]
    public async Task ANameSequenceIsTheDatabasesAlone()
    {
        // No login may write seq itself (DL-101): an identity on SQL Server, GENERATED ALWAYS on PostgreSQL.
        var table = IsSqlServer ? "[request].[AccountNameAssignment]" : "request.account_name_assignment";
        var refused = await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.RequestAppConnectionString, $"INSERT INTO {table} ({Col("Seq")}) VALUES (1)", Ct));
        refused.Message.ShouldContain(IsSqlServer ? "IDENTITY_INSERT" : "non-DEFAULT");
    }

    [Fact]
    public async Task AGrantLockAnotherRunHoldsIsNotTaken()
    {
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        await using (var context = ProvisioningContext())
        {
            context.GrantLocks.Add(GrantLock.Take(planned.AccountId!.Value, planned.EntitlementId!.Value, Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1)));
            await context.SaveChangesAsync(Ct);
        }

        var target = new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied" };
        (await RunAsync(item, target)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Planned, "it waits for the lock");
        target.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task AGrantLockIsRenewedWithEachCallItProtects()
    {
        // The add runs and the read doesn't match; the undo's removal call takes 50 seconds and breaks off, so it is read back.
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var probe = new RenewalProbeTarget(clock, async () =>
        {
            await using var context = ProvisioningContext();
            return (await context.GrantLocks.AsNoTracking().SingleAsync(l => l.AccountId == planned.AccountId && l.EntitlementId == planned.EntitlementId, Ct)).UntilUtc;
        });
        (await RunAsync(item, probe, clock)).ShouldBeTrue();

        // When the removal is read back, the lock runs a whole lease from then, not from when it was taken.
        probe.SeenAt.ShouldNotBeNull();
        probe.HeldUntil!.Value.ShouldBeGreaterThanOrEqualTo(probe.SeenAt!.Value.AddSeconds(59));
        (await PlannedAsync(item)).Item.Problem.ShouldBe("VerifyMismatch");
    }

    /// <summary>Another item on the same account and group as <paramref name="item"/>, with its add: succeeded, or still planned.</summary>
    private async Task<Guid> HolderAsync(Guid item, bool succeeded)
    {
        var (planned, operations) = await PlannedAsync(item);
        var add = operations.Single(o => o.Action == ProvisioningPlanner.EntitlementAdd);
        await using var context = ProvisioningContext();
        var now = DateTimeOffset.UtcNow;
        var plan = ProvisioningPlanner.ForExistingAccount(Guid.CreateVersion7(), 1,
            new ItemDetails(planned.Beneficiary!, planned.ApplicationId!.Value, planned.EntitlementId!.Value, planned.EntitlementVersionId!.Value, planned.AccountId, null, null),
            add.TargetId, add.RelatedId!, ConnectorBinding.Create(planned.ApplicationId!.Value, "mock-ad", AccountContainer, GroupContainer), now);
        var holder = plan.Item;
        var holderAdd = plan.Operations.Single();
        context.Items.Add(holder);
        context.Operations.Add(holderAdd);
        await context.SaveChangesAsync(Ct);
        if (succeeded)
        {
            holder.Claim(now, TimeSpan.FromMinutes(1));
            await context.SaveChangesAsync(Ct);
            holderAdd.Start();
            await context.SaveChangesAsync(Ct);
            holderAdd.Complete(ProvisioningPlanner.Applied, now);
            holder.Succeed(null, now);
            await context.SaveChangesAsync(Ct);
        }

        return holder.Id;
    }

    /// <summary>
    /// What a forger holding the provisioning login can do: store the item under another name, mark its create done and append
    /// a matching, intact audit record.
    /// </summary>
    private async Task ForgeCreatedAsync(World world, Guid item, string forgedName)
    {
        var actions = (await IntegrityAsync(item)).Actions;
        var binding = Bound(world).Single();
        var (decider, contexts) = Decider([binding]);
        ProvisioningPlan genuine;
        try
        {
            genuine = (await decider.DecideAsync(item, actions, DateTimeOffset.UtcNow, Ct)).ShouldNotBeNull();
        }
        finally
        {
            foreach (var c in contexts)
            {
                await c.DisposeAsync();
            }
        }

        var facts = genuine.Item;
        var forged = ProvisioningPlanner.ForNewAccount(item, actions,
            new ItemDetails(facts.Beneficiary!, facts.ApplicationId!.Value, facts.EntitlementId!.Value, facts.EntitlementVersionId!.Value, null, facts.NewAccountType, forgedName),
            genuine.Operations[1].RelatedId!, binding, DateTimeOffset.UtcNow);
        await using var context = ProvisioningContext();
        context.Items.Add(forged.Item);
        context.Operations.AddRange(forged.Operations);
        await context.SaveChangesAsync(Ct);
        var create = forged.Operations[0];
        create.Start();
        await context.SaveChangesAsync(Ct);
        create.Complete(ProvisioningPlanner.Applied, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync(Ct);

        var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor("system:provisioning"),
            new AuditActionCatalog(ProvisioningAuditActions.All.Select(a => new AuditActionRegistration(a))));
        await using var tx = await context.Database.BeginTransactionAsync(Ct);
        await writer.AppendAsync(
            tx.GetDbTransaction(),
            new AuditEventRecord
            {
                Action = ProvisioningAuditActions.StepDone,
                TargetType = ProvisioningAuditActions.Target,
                TargetId = item.ToString(),
                Outcome = AuditOutcome.Succeeded,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid().ToString("N"),
                Payload =
                [
                    new AuditPayloadField("operationId", create.Id.ToString(), AuditFieldClass.Plain),
                    new AuditPayloadField("fingerprint", ProvisioningPlanner.Fingerprint(create), AuditFieldClass.Plain),
                    new AuditPayloadField("code", ProvisioningPlanner.Applied, AuditFieldClass.Plain),
                ],
            },
            Ct);
        await tx.CommitAsync(Ct);
    }

    private Modules.Provisioning.Infrastructure.Persistence.ProvisioningDbContext ProvisioningContext() =>
        TestSupport.CreateProvisioning(db.Provider, db.ProvisioningAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor("system:provisioning")));

    /// <summary>A target whose calls for one action reach the directory, then the runner is stopped before it hears back.</summary>
    private sealed class StoppingAfterTarget(IProvisioningTarget inner, string action, CancellationTokenSource stop) : IProvisioningTarget
    {
        public async Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken)
        {
            var result = await inner.RunAsync(operation, cancellationToken);
            if (operation.Action == action)
            {
                await stop.CancelAsync();
                stop.Token.ThrowIfCancellationRequested();
            }

            return result;
        }

        public Task<bool?> VerifyAsync(TargetStep operation, CancellationToken cancellationToken) => inner.VerifyAsync(operation, cancellationToken);

        public Task<bool?> ExistsAsync(string objectType, string id, CancellationToken cancellationToken) => inner.ExistsAsync(objectType, id, cancellationToken);
    }

    /// <summary>Adds apply and read as missing; a removal takes 50 seconds and breaks off, and its read-back notes the lock's end.</summary>
    private sealed class RenewalProbeTarget(MovingClock clock, Func<Task<DateTimeOffset>> lockUntil) : IProvisioningTarget
    {
        public DateTimeOffset? SeenAt { get; private set; }

        public DateTimeOffset? HeldUntil { get; private set; }

        public Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken)
        {
            if (operation.Action != ProvisioningPlanner.EntitlementRemove)
            {
                return Task.FromResult(new StepResult(StepStatus.Done, ProvisioningPlanner.Applied));
            }

            clock.Advance(TimeSpan.FromSeconds(50));
            throw new TimeoutException("The call broke off.");
        }

        public async Task<bool?> VerifyAsync(TargetStep operation, CancellationToken cancellationToken)
        {
            if (operation.Action != ProvisioningPlanner.EntitlementRemove)
            {
                return false;
            }

            if (SeenAt is null)
            {
                SeenAt = clock.GetUtcNow();
                HeldUntil = await lockUntil();
            }

            return true;
        }

        public Task<bool?> ExistsAsync(string objectType, string id, CancellationToken cancellationToken) => Task.FromResult<bool?>(true);
    }

    /// <summary>A target whose calls for one action always break off, so their outcome is never known.</summary>
    private sealed class BreakingTarget(IProvisioningTarget inner, string action) : IProvisioningTarget
    {
        public Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken) =>
            operation.Action == action ? throw new TimeoutException("The call broke off.") : inner.RunAsync(operation, cancellationToken);

        public Task<bool?> VerifyAsync(TargetStep operation, CancellationToken cancellationToken) => inner.VerifyAsync(operation, cancellationToken);

        public Task<bool?> ExistsAsync(string objectType, string id, CancellationToken cancellationToken) => inner.ExistsAsync(objectType, id, cancellationToken);
    }
}
