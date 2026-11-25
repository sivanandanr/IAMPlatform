using System.Diagnostics;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Account.Contracts;
using IAM.Modules.Account.Infrastructure.Accounts;
using IAM.Modules.Administration.Contracts;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Modules.Request.Application.Items;
using IAM.Modules.Workflow.Contracts;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 8 slice 5b (DL-094) against both engines: the runner claims due items with a lease, runs their steps in order on a
/// scripted target, retries temporary failures with a growing delay, fails what a person must look at, settles unknown
/// outcomes and interrupted steps by reading the target, and checks the target before succeeding. Accounts it created are
/// recorded once, verified. The IAM team reads blocked and failed items only with the permission.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task ATemporaryFaultIsRetriedLaterThenTheItemSucceeds()
    {
        var item = await PlannedExistingAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var target = new ScriptedTarget(StepStatus.Retry, StepStatus.Done);

        (await RunAsync(item, target, clock)).ShouldBeTrue();
        var (waiting, steps) = await PlannedAsync(item);
        waiting.State.ShouldBe(ProvisioningState.Planned);
        waiting.Attempts.ShouldBe(1);
        waiting.NextAttemptAtUtc.ShouldNotBeNull().ShouldBeGreaterThan(clock.GetUtcNow());
        steps.Single().State.ShouldBe(ProvisioningState.Planned);

        (await RunAsync(item, target, clock)).ShouldBeFalse("not due yet");
        clock.Advance(TimeSpan.FromMinutes(5));
        (await RunAsync(item, target, clock)).ShouldBeTrue();

        var (done, finished) = await PlannedAsync(item);
        done.State.ShouldBe(ProvisioningState.Succeeded);
        done.FinishedAtUtc.ShouldNotBeNull();
        done.RecordedAccountId.ShouldBeNull("an existing account records nothing");
        finished.Single().ShouldSatisfyAllConditions(s => s.State.ShouldBe(ProvisioningState.Succeeded), s => s.ResultCode.ShouldBe("Done"));
        target.Runs.ShouldBe(2, "the same operation id both times");
        (await AuditCountAsync(item.ToString(), ProvisioningAuditActions.StepRetry, "Failed")).ShouldBe(1);
        (await AuditCountAsync(item.ToString(), ProvisioningAuditActions.Succeeded, "Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task ARejectionOrRunningOutOfAttemptsFailsTheItemForAPerson()
    {
        var rejected = await PlannedExistingAsync();
        (await RunAsync(rejected, new ScriptedTarget(StepStatus.Rejected))).ShouldBeTrue();
        (await PlannedAsync(rejected)).ShouldSatisfyAllConditions(
            p => p.Item.State.ShouldBe(ProvisioningState.Failed),
            p => p.Item.Problem.ShouldBe("Rejected"),
            p => p.Operations.Single().State.ShouldBe(ProvisioningState.Failed));
        (await AuditCountAsync(rejected.ToString(), ProvisioningAuditActions.Failed, "Failed")).ShouldBe(1);

        var exhausted = await PlannedExistingAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var target = new ScriptedTarget(StepStatus.Retry, StepStatus.Retry, StepStatus.Done);
        (await RunAsync(exhausted, target, clock, maxAttempts: 2)).ShouldBeTrue();
        clock.Advance(TimeSpan.FromHours(2));
        (await RunAsync(exhausted, target, clock, maxAttempts: 2)).ShouldBeTrue();
        (await PlannedAsync(exhausted)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("RetriesExhausted"));
        clock.Advance(TimeSpan.FromHours(2));
        (await RunAsync(exhausted, target, clock, maxAttempts: 2)).ShouldBeFalse("a failed item is a person's, never run again");
    }

    [Fact]
    public async Task AnUnknownOutcomeOrAnInterruptedAddRunsAgainUnderItsOperationId()
    {
        // The call broke off. The membership may be there, but a read can't say this operation made it (DL-101): the add is
        // tried again later under its operation id, and the connector's ledger answers.
        var unknown = await PlannedExistingAsync();
        var target = new ScriptedTarget(StepStatus.Unknown) { Matches = true };
        (await RunAsync(unknown, target)).ShouldBeTrue();
        (await PlannedAsync(unknown)).Item.State.ShouldBe(ProvisioningState.Planned);
        target.Runs.ShouldBe(1);

        // A runner stopped in the middle of the add (here: shut down): the item stays running until its lease expires, then the
        // next runner runs it again under the same operation id rather than reading the target (DL-101).
        var interrupted = await PlannedExistingAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        using (var stop = new CancellationTokenSource())
        {
            var stopping = new ScriptedTarget(StepStatus.Done) { OnRun = stop.Cancel };
            await Should.ThrowAsync<OperationCanceledException>(() => RunAsync(interrupted, stopping, clock, cancellationToken: stop.Token));
        }

        var (running, steps) = await PlannedAsync(interrupted);
        running.State.ShouldBe(ProvisioningState.Running);
        steps.Single().State.ShouldBe(ProvisioningState.Running);
        var next = new ScriptedTarget(StepStatus.Done) { DoneCode = "Applied", Matches = true };
        (await RunAsync(interrupted, next, clock)).ShouldBeFalse("its lease hasn't expired");
        clock.Advance(TimeSpan.FromMinutes(10));
        (await RunAsync(interrupted, next, clock)).ShouldBeTrue();
        var (done, doneSteps) = await PlannedAsync(interrupted);
        done.State.ShouldBe(ProvisioningState.Succeeded);
        doneSteps.Single().ResultCode.ShouldBe(ProvisioningPlanner.Applied, "the ledger's answer, not a read's");
        next.Runs.ShouldBe(1, "run again under its operation id");
    }

    [Fact]
    public async Task TheTargetMustMatchThePlanBeforeAnythingSucceeds()
    {
        var item = await PlannedExistingAsync();
        (await RunAsync(item, new ScriptedTarget(StepStatus.Done) { Matches = false })).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("VerifyMismatch"));

        // An unknown connector key (a binding the worker doesn't run) fails rather than waits forever.
        var orphan = await PlannedExistingAsync();
        (await RunAsync(orphan, null)).ShouldBeTrue();
        (await PlannedAsync(orphan)).Item.Problem.ShouldBe("ConnectorUnavailable");
    }

    [Fact]
    public async Task TwoRunnersNeverRunTheSameItem()
    {
        var item = await PlannedExistingAsync();
        var target = new ScriptedTarget(StepStatus.Done);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunAsync(item, target)));
        results.Count(r => r).ShouldBe(1);
        target.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task ACreatedAccountIsRecordedOnceVerifiedAndNeverOverAnother()
    {
        var world = await WorldAsync();
        var created = new CreatedAccount(Guid.CreateVersion7(), world.Application, "Secondary", $"adm.{Guid.NewGuid():N}"[..18], $"CN=adm-{Guid.NewGuid():N},OU=Users,DC=corp,DC=test", world.Beneficiary);
        var correlation = ActivityTraceId.CreateRandom().ToHexString();
        Guid first;
        await using (var accounts = TestSupport.CreateAccount(db.Provider, db.AccountAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor("system:provisioning"))))
        {
            var service = AccountRecorder(accounts);
            first = await service.RecordCreatedAsync(created, correlation, Ct);
            (await service.RecordCreatedAsync(created, correlation, Ct)).ShouldBe(first, "the same item records once");
            (await Should.ThrowAsync<AccountNotRecordedException>(() => service.RecordCreatedAsync(created with { Name = "other.name" }, correlation, Ct))).Code.ShouldBe("ItemRecordedDifferently");
            (await Should.ThrowAsync<AccountNotRecordedException>(() => service.RecordCreatedAsync(created with { ProvisioningItemId = Guid.CreateVersion7(), Name = created.Name.ToUpperInvariant() }, correlation, Ct))).Code.ShouldBe("NameInUse");
            (await Should.ThrowAsync<AccountNotRecordedException>(() => service.RecordCreatedAsync(created with { ProvisioningItemId = Guid.CreateVersion7(), Name = "x.other" }, correlation, Ct))).Code.ShouldBe("TargetInUse");
            (await Should.ThrowAsync<AccountNotRecordedException>(() => service.RecordCreatedAsync(created with { ProvisioningItemId = Guid.CreateVersion7(), Type = "Primary", Name = "p.other", TargetId = "CN=p-other" }, correlation, Ct))).Code.ShouldBe("PrimaryExists");
        }

        await using (var accounts = TestSupport.CreateAccount(db.Provider, db.AccountAppConnectionString))
        {
            var record = (await new AccountDirectory(accounts).FindAsync(first, Ct)).ShouldNotBeNull();
            record.ShouldSatisfyAllConditions(r => r.Verified.ShouldBeTrue(), r => r.Status.ShouldBe("Active"), r => r.Owner.ShouldBe(world.Beneficiary), r => r.Type.ShouldBe("Secondary"));
        }

        (await AuditCountAsync(first.ToString(), AccountProvisioningService.Created, "Succeeded")).ShouldBe(1);

        // The database accepts a created account only active, verified and from a provisioning item; nothing changes which item.
        var table = IsSqlServer ? "[account].[Account]" : "account.account";
        foreach (var login in new[] { db.AccountAppConnectionString, db.MigratorConnectionString })
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login,
                $"UPDATE {table} SET {Col("ProvisioningItemId")} = '{Guid.NewGuid()}' WHERE {Col("Id")} = '{first}'", Ct))).Message.ShouldNotBeNullOrEmpty();
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login,
                $"UPDATE {table} SET {Col("ProvisioningItemId")} = NULL WHERE {Col("Id")} = '{first}'", Ct))).Message.ShouldNotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task ANameTakenSinceTheApprovalBlocksTheNewAccount()
    {
        var world = await WorldAsync();
        var name = $"adm.{Guid.NewGuid():N}"[..18];
        var route = new FixedMatrix(Route(Stage("EntitlementOwner"), Stage("EntitlementOwner", sequence: 2, kind: "Action", fields: ["AccountName"])));
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Privileged, null, "Secondary", null, null), route);
        await NameAndCompleteAsync(world, item, route, name);

        // Meanwhile someone links an existing account with that name, in another spelling.
        var permissions = new Grants((Admin, AccessActions.AccountsManage), (Checker, AccessActions.AccountsApprove));
        await using (var a = Accounts(Admin, permissions))
        {
            var link = await a.Service.ProposeLinkAsync(world.Application, "Secondary", name.ToUpperInvariant(), $"CN={name}-other", $"x-{Guid.NewGuid():N}"[..12], "Found in AD", Ct);
            await using var k = Accounts(Checker, permissions);
            await k.Service.ApproveLinkAsync(link.Id, null, Ct);
        }

        (await HandleAsync(world, await WorkflowEnvelopeAsync(item, ApprovalClosed.MessageType))).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(item)).Item.Problem.ShouldBe("NameInUse");
    }

    [Fact]
    public async Task OnlyTheIamTeamReadsBlockedAndFailedItems()
    {
        var world = await WorldAsync();
        var manual = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null));
        await HandleAsync(world, await WorkflowEnvelopeAsync(manual, ApprovalAuthorized.MessageType), ConnectorBinding.Create(Guid.CreateVersion7(), "mock-ad", AccountContainer, GroupContainer));

        await using (var context = TestSupport.CreateProvisioning(db.Provider, db.ProvisioningAppConnectionString))
        {
            var allowed = new ProvisioningItemQueries(context, new FixedActor("iam-manager"), new Grants(("iam-manager", AccessActions.ProvisioningItemsRead)));
            var view = (await allowed.FindAsync(manual, Ct)).ShouldNotBeNull();
            view.ShouldSatisfyAllConditions(v => v.State.ShouldBe("Blocked"), v => v.Problem.ShouldBe("ManualWork"), v => v.Steps.ShouldBeEmpty());
            (await allowed.ListAsync("Blocked", 1, 100, Ct)).Items.ShouldContain(i => i.Id == manual);
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => allowed.ListAsync("Planned", 1, 10, Ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => allowed.ListAsync("Blocked", 1, 101, Ct));

            var denied = new ProvisioningItemQueries(context, new FixedActor(world.Beneficiary), new Grants());
            await Should.ThrowAsync<ProvisioningAccessDeniedException>(() => denied.FindAsync(manual, Ct));
            await Should.ThrowAsync<ProvisioningAccessDeniedException>(() => denied.ListAsync("Blocked", 1, 10, Ct));
        }
    }

    // ---- Running helpers ---------------------------------------------------------------------------------------------------

    /// <summary>An approved, planned item for an existing account, in its own world (one waiting item per person and entitlement).</summary>
    private async Task<Guid> PlannedExistingAsync()
    {
        var world = await WorldAsync();
        var item = await ApprovedAsync(world, new SubmitItem(world.Beneficiary, world.Standard, world.Primary, null, null, null));
        (await HandleAsync(world, await WorkflowEnvelopeAsync(item, ApprovalAuthorized.MessageType))).ShouldBe(MessageOutcome.Processed);
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Planned);
        return item;
    }

    private async Task<bool> RunAsync(Guid item, IProvisioningTarget? target, TimeProvider? clock = null, int maxAttempts = 5, CancellationToken? cancellationToken = null, int? stepTimeoutSeconds = null, IProvisioningTargets? targets = null, IApprovalIntegrity? integrity = null, int? undoMaxAttempts = null, int? maxPostponements = null, string? groupContainer = null)
    {
        const string actor = "system:provisioning";
        clock ??= TimeProvider.System;
        await using var context = TestSupport.CreateProvisioning(db.Provider, db.ProvisioningAppConnectionString, new AuditingSaveChangesInterceptor(clock, new FixedActor(actor)));
        await using var accounts = TestSupport.CreateAccount(db.Provider, db.AccountAppConnectionString, new AuditingSaveChangesInterceptor(clock, new FixedActor(actor)));
        var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), clock, new FixedActor(actor),
            new AuditActionCatalog(ProvisioningAuditActions.All.Select(a => new AuditActionRegistration(a))));
        // The runner decides again before it runs (DL-096), with the binding the item was planned with.
        var application = (await context.Items.AsNoTracking().SingleAsync(i => i.Id == item, Ct)).ApplicationId ?? Guid.CreateVersion7();
        var binding = ConnectorBinding.Create(application, "mock-ad", AccountContainer, groupContainer ?? GroupContainer);
        var (decider, contexts) = Decider([binding], clock, integrity);
        try
        {
            await using var audit = TestSupport.CreateAudit(db.Provider, db.AuditAppConnectionString);
            var runner = new ProvisioningRunner(
                context,
                writer,
                new AuditTrail(audit),
                clock,
                targets ?? new Targets(target),
                AccountRecorder(accounts, clock),
                new AccountDirectory(accounts),
                new ConnectorBindings([binding]),
                decider,
                new ProvisioningRunnerSettings(batchSize: 10, leaseSeconds: 60, maxAttempts: maxAttempts, retryDelaySeconds: 30, stepTimeoutSeconds: stepTimeoutSeconds, undoMaxAttempts: undoMaxAttempts, maxPostponements: maxPostponements),
                TestSupport.ProviderFor(db.Provider),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProvisioningRunner>.Instance);
            return await runner.RunItemAsync(item, cancellationToken ?? Ct);
        }
        finally
        {
            foreach (var c in contexts)
            {
                await c.DisposeAsync();
            }
        }
    }

    private AccountProvisioningService AccountRecorder(Modules.Account.Infrastructure.Persistence.AccountDbContext accounts, TimeProvider? clock = null) =>
        new(accounts,
            new AuditWriter(TestSupport.ProviderFor(db.Provider), clock ?? TimeProvider.System, new FixedActor("system:provisioning"), new AuditActionCatalog([new AuditActionRegistration(AccountProvisioningService.Created)])),
            clock ?? TimeProvider.System,
            TestSupport.ProviderFor(db.Provider));

    private sealed class Targets(IProvisioningTarget? target) : IProvisioningTargets
    {
        public IProvisioningTarget? Find(string connectorKey) => connectorKey == "mock-ad" ? target : null;
    }

    /// <summary>A target that answers each run with the next scripted status (the last repeats); an Unknown throws, as a broken call.</summary>
    private sealed class ScriptedTarget(params StepStatus[] script) : IProvisioningTarget
    {
        private int _runs;

        public int Runs => _runs;

        public bool Matches { get; init; } = true;

        public Action? OnRun { get; init; }

        /// <summary>How long each call takes, honouring the runner's time limit.</summary>
        public TimeSpan Delay { get; init; }

        public async Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken)
        {
            var status = script[Math.Min(Interlocked.Increment(ref _runs) - 1, script.Length - 1)];
            OnRun?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return status == StepStatus.Unknown
                ? throw new TimeoutException("The call broke off.")
                : new StepResult(status, status == StepStatus.Done && DoneCode is not null ? DoneCode : status.ToString());
        }

        /// <summary>The code a done step reports (the connector's Applied or AlreadyInState); else "Done".</summary>
        public string? DoneCode { get; init; }

        /// <summary>Per-step answers to a read of the target, overriding <see cref="Matches"/>.</summary>
        public Func<TargetStep, bool?>? MatchesStep { get; init; }

        /// <summary>Whether reads of the target fail (an unreachable directory).</summary>
        public bool ReadsFail { get; init; }

        public Task<bool?> VerifyAsync(TargetStep operation, CancellationToken cancellationToken) =>
            ReadsFail ? throw new TimeoutException("The read broke off.") : Task.FromResult(MatchesStep is null ? Matches : MatchesStep(operation));

        /// <summary>Whether objects are on the target (an existing account is read before anything is granted to it).</summary>
        public bool Exists { get; init; } = true;

        public Task<bool?> ExistsAsync(string objectType, string id, CancellationToken cancellationToken) =>
            ReadsFail ? throw new TimeoutException("The read broke off.") : Task.FromResult<bool?>(Exists);
    }

    private sealed class MovingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
