using IAM.BuildingBlocks.Persistence;
using IAM.Connectors.Sdk;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Workers.Provisioning;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 9 slice 8 (DL-103), the DL-102 re-review's findings, on both engines: a write that fails after it landed is unknown
/// whatever the connector's code, except a throttle; an unsettled step that runs out of retries is unknown, and the mark is
/// audited; an existing account's item holds its grant lock through its final read; and steps in flight at the upgrade are
/// marked.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task AWriteThatFailsAfterItLandedIsUnknownWhateverItsCode()
    {
        // The create lands, then the connection drops (TargetUnavailable) and the connector's ledger keeps nothing.
        var (item, name, mock, _) = await PlannedNewAccountAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var dropping = new FaultAfterWriteConnector(mock, ActionCatalog.AccountCreate, ResultCodes.TargetUnavailable);
        (await RunAsync(item, new ConnectorTarget(dropping), clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Operations.Single(o => o.Step == 1).Unsettled.ShouldBeTrue();

        // Run again, the SDK refuses the create (AlreadyExists): the object may be this item's own, so a person reconciles it.
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, new ConnectorTarget(mock), clock)).ShouldBeTrue();
        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe(ProvisioningRunner.OutcomeUnknown));
        steps.Single(s => s.Step == 1).ResultCode.ShouldBe(ProvisioningRunner.OutcomeUnknown);
        (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN={name},{AccountContainer}"), Ct)).ShouldNotBeNull("left for a person");

        // A throttle comes before any write: a plain retry, not unsettled.
        var (throttledItem, _, throttledMock, _) = await PlannedNewAccountAsync();
        var throttled = new FaultAfterWriteConnector(throttledMock, ActionCatalog.AccountCreate, ResultCodes.Throttled, writes: false);
        (await RunAsync(throttledItem, new ConnectorTarget(throttled), clock)).ShouldBeTrue();
        (await PlannedAsync(throttledItem)).Operations.Single(o => o.Step == 1).ShouldSatisfyAllConditions(
            o => o.State.ShouldBe(ProvisioningState.Planned),
            o => o.Unsettled.ShouldBeFalse());
    }

    [Fact]
    public async Task AnUnsettledStepThatRunsOutOfRetriesIsUnknownAndTheMarkIsAudited()
    {
        // The add's call breaks off, then fails for now on its last attempt.
        var item = await PlannedExistingAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var target = new ScriptedTarget(StepStatus.Unknown, StepStatus.Retry);
        (await RunAsync(item, target, clock, maxAttempts: 2)).ShouldBeTrue();
        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, target, clock, maxAttempts: 2)).ShouldBeTrue();

        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe(ProvisioningRunner.OutcomeUnknown));
        steps.Single().ResultCode.ShouldBe(ProvisioningRunner.OutcomeUnknown);
        steps.ShouldNotContain(s => s.Step > 10, "nothing removed");

        // The retry that left it unsettled says so in the audit.
        await using var audit = TestSupport.CreateAudit(db.Provider, db.AuditAppConnectionString);
        (await new AuditTrail(audit).ForTargetAsync(ProvisioningAuditActions.Target, item.ToString(), 100, Ct))
            .Where(e => e.Action == ProvisioningAuditActions.StepRetry && e.Payload.ContainsKey("unsettled"))
            .ShouldContain(e => e.Payload["unsettled"] == "true");
    }

    [Fact]
    public async Task TheGrantLockIsHeldThroughTheFinalReadAndReleasedAfter()
    {
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        async Task<GrantLock?> LockAsync()
        {
            await using var context = ProvisioningContext();
            return await context.GrantLocks.AsNoTracking().SingleOrDefaultAsync(l => l.AccountId == planned.AccountId && l.EntitlementId == planned.EntitlementId, Ct);
        }

        var probe = new FinalReadProbe(LockAsync);
        (await RunAsync(item, probe)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Succeeded);

        // While the final read ran, this item held the lock; no undo could remove the access before the success was saved.
        probe.Seen.ShouldNotBeNull().HeldBy.ShouldBe(item);
        probe.Seen!.UntilUtc.ShouldBeGreaterThan(probe.SeenAt!.Value);
        (await LockAsync()).ShouldNotBeNull().UntilUtc.ShouldBeLessThan(DateTimeOffset.UtcNow, "released once saved");
    }

    [Fact]
    public async Task StepsInFlightAtTheUpgradeAreMarkedUnsettled()
    {
        // One item was tried (a definite failure for now, so not marked by the runner); another never ran.
        var tried = await PlannedExistingAsync();
        (await RunAsync(tried, new ScriptedTarget(StepStatus.Retry))).ShouldBeTrue();
        var fresh = await PlannedExistingAsync();
        (await PlannedAsync(tried)).Operations.Single().Unsettled.ShouldBeFalse();

        // The upgrade's backfill (exactly the migration's statement) marks only what was tried.
        await db.ExecuteAsync(db.ProvisioningAppConnectionString,
            IsSqlServer ? IAM.Persistence.SqlServer.Migrations.Provisioning.UnsettledBackfill.Sql : IAM.Persistence.PostgreSql.Migrations.Provisioning.UnsettledBackfill.Sql, Ct);
        (await PlannedAsync(tried)).Operations.Single().Unsettled.ShouldBeTrue();
        (await PlannedAsync(fresh)).Operations.Single().Unsettled.ShouldBeFalse();
    }

    /// <summary>A connector whose first write of one action lands (under another id, so its ledger keeps nothing), then fails with a code.</summary>
    private sealed class FaultAfterWriteConnector(IConnector inner, string action, string code, bool writes = true) : IConnector
    {
        private bool _faulted;

        public ConnectorCapabilities Capabilities => inner.Capabilities;

        public Task<ConnectorHealth> CheckHealthAsync(CancellationToken cancellationToken) => inner.CheckHealthAsync(cancellationToken);

        public Task<ObjectSnapshot?> ReadAsync(ObjectRef target, CancellationToken cancellationToken) => inner.ReadAsync(target, cancellationToken);

        public async Task<OperationResult> ExecuteAsync(ConnectorOperation operation, CancellationToken cancellationToken)
        {
            if (operation.Action != action || _faulted)
            {
                return await inner.ExecuteAsync(operation, cancellationToken);
            }

            _faulted = true;
            if (writes)
            {
                await inner.ExecuteAsync(operation with { OperationId = Guid.CreateVersion7() }, cancellationToken);
            }

            return OperationResult.Of(operation, OperationStatus.Failed, code);
        }

        public Task<VerificationResult> VerifyAsync(ConnectorOperation operation, CancellationToken cancellationToken) => inner.VerifyAsync(operation, cancellationToken);

        public Task<ObjectPage> ListAsync(string objectType, string? continuation, int pageSize, CancellationToken cancellationToken) =>
            inner.ListAsync(objectType, continuation, pageSize, cancellationToken);
    }

    /// <summary>Adds apply; the first read of the add notes the grant lock as it stands, then matches.</summary>
    private sealed class FinalReadProbe(Func<Task<GrantLock?>> readLock) : IProvisioningTarget
    {
        public GrantLock? Seen { get; private set; }

        public DateTimeOffset? SeenAt { get; private set; }

        public Task<StepResult> RunAsync(TargetStep operation, CancellationToken cancellationToken) =>
            Task.FromResult(new StepResult(StepStatus.Done, ProvisioningPlanner.Applied));

        public async Task<bool?> VerifyAsync(TargetStep operation, CancellationToken cancellationToken)
        {
            if (SeenAt is null)
            {
                SeenAt = DateTimeOffset.UtcNow;
                Seen = await readLock();
            }

            return true;
        }

        public Task<bool?> ExistsAsync(string objectType, string id, CancellationToken cancellationToken) => Task.FromResult<bool?>(true);
    }
}
