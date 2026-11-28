using System.Diagnostics.CodeAnalysis;
using IAM.Connectors.ActiveDirectory.Mock;
using IAM.Connectors.Sdk;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Domain.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using IAM.Workers.Provisioning;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 9 slices 11 to 13 (DL-106 to DL-108), the re-reviews' findings: a damaged stored record of a step, from the durable
/// ledger through the SDK and the connector target, stops the item for a person, never retried or settled by a read; on a new
/// account, what the item did is undone first; under a clock that doesn't move, a lock taken by another run still stops this
/// run before its next call and before its undo removes anything; and the upgrade stops when a failed operation is stored.
/// </summary>
public sealed partial class RequestItemParityTests
{
    [Fact]
    public async Task ADamagedLedgerRecordStopsTheItemForAPersonThroughTheWholeChain()
    {
        // The real chain (DL-107): the durable ledger reads a stored failure and refuses it, the SDK passes the refusal on, the
        // connector target names it, and the runner stops the item. Nothing is written: the ledger is read before the target.
        var (item, name, _, group) = await PlannedNewAccountAsync();
        var services = new ServiceCollection().AddSingleton<IProvisioningLedger>(new DamagedLedger()).BuildServiceProvider();
        await using var _ = services;
        var mock = new MockActiveDirectoryConnector(
            ConnectorSettings.Default, new DurableOperationLedger(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System), "DC=corp,DC=test");
        mock.AddContainer("Users");
        mock.AddGroup(group["CN=".Length..group.IndexOf(',', StringComparison.Ordinal)], mock.AddContainer("Groups"));

        (await RunAsync(item, new ConnectorTarget(mock))).ShouldBeTrue();

        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe(ProvisioningRunner.LedgerDamaged));
        steps.Single(s => s.Step == 1).ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(ProvisioningState.Failed),
            s => s.ResultCode.ShouldBe(ProvisioningRunner.LedgerDamaged));
        (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN={name},{AccountContainer}"), Ct)).ShouldBeNull("nothing was written");
        await using var audit = TestSupport.CreateAudit(db.Provider, db.AuditAppConnectionString);
        (await new AuditTrail(audit).ForTargetAsync(ProvisioningAuditActions.Target, item.ToString(), 100, Ct))
            .ShouldSatisfyAllConditions(
                e => e.ShouldContain(a => a.Action == ProvisioningAuditActions.Failed),
                e => e.ShouldNotContain(a => a.Action == ProvisioningAuditActions.StepRetry, "never retried"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ADamagedRecordOfALaterStepOnANewAccountUndoesTheAccountThroughTheWholeChain(int damaged)
    {
        // DL-108: the create ran; the stored record of the add (step 2) or the enable (step 3) is damaged. Everything on the
        // account is this item's, so the undo removes the membership and disables the account, and the item fails for a person.
        var (item, name, _, group) = await PlannedNewAccountAsync();
        var (_, planned) = await PlannedAsync(item);
        var mock = DurableMock(group, planned.Single(s => s.Step == damaged).Id, out var services);
        await using var __ = services;

        (await RunAsync(item, new ConnectorTarget(mock))).ShouldBeTrue();

        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe(ProvisioningRunner.LedgerDamaged));
        steps.Single(s => s.Step == damaged).ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(ProvisioningState.Failed),
            s => s.ResultCode.ShouldBe(ProvisioningRunner.LedgerDamaged));
        steps.Where(s => s.Step > ProvisioningPlanner.MaxPlanSteps).Select(s => (s.Action, s.State))
            .ShouldBe([(ProvisioningPlanner.EntitlementRemove, ProvisioningState.Succeeded), (ProvisioningPlanner.AccountDisable, ProvisioningState.Succeeded)]);
        var account = (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN={name},{AccountContainer}"), Ct)).ShouldNotBeNull("the create ran");
        account.Enabled.ShouldBe(false);
        account.Links.ShouldNotContain(l => string.Equals(l.Id, group, StringComparison.OrdinalIgnoreCase));
        (await FinalAuditAsync(item)).ShouldSatisfyAllConditions(
            p => p["step"].ShouldBe(damaged.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            p => p["code"].ShouldBe(ProvisioningRunner.LedgerDamaged));
    }

    [Fact]
    public async Task AnUndoAfterDamageThatHasToWaitStillNamesTheDamagedStepWhenItEnds()
    {
        // DL-108: the enable's record is damaged and the undo's removal breaks off, so the undo waits for a later run. That run
        // finishes it; the item fails LedgerDamaged and its final audit still names the damaged step.
        var (item, name, _, group) = await PlannedNewAccountAsync();
        var (_, planned) = await PlannedAsync(item);
        var mock = DurableMock(group, planned.Single(s => s.Step == 3).Id, out var services);
        await using var __ = services;
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var broke = false;
        var breaking = new HookedTarget(new ConnectorTarget(mock), step =>
        {
            if (step.Action == ProvisioningPlanner.EntitlementRemove && !broke)
            {
                broke = true;
                throw new TimeoutException("The call broke off.");
            }

            return Task.CompletedTask;
        });

        (await RunAsync(item, breaking, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(
            i => i.State.ShouldBe(ProvisioningState.Planned, "the undo waits"),
            i => i.UndoReason.ShouldBe(ProvisioningRunner.LedgerDamaged));

        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, breaking, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.ShouldSatisfyAllConditions(
            i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe(ProvisioningRunner.LedgerDamaged));
        var account = (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN={name},{AccountContainer}"), Ct)).ShouldNotBeNull();
        account.Enabled.ShouldBe(false);
        account.Links.ShouldNotContain(l => string.Equals(l.Id, group, StringComparison.OrdinalIgnoreCase));
        (await FinalAuditAsync(item)).ShouldSatisfyAllConditions(
            p => p["step"].ShouldBe("3", "the resumed undo names the damaged step"),
            p => p["code"].ShouldBe(ProvisioningRunner.LedgerDamaged));
    }

    [Fact]
    public async Task ADamagedRecordOfAnAddIsNeverSettledByAReadOrRetried()
    {
        // The target says the add's stored record is damaged: no read settles it and it is not tried again. It is the only step
        // on an existing account, so nothing is undone and a person reconciles it. The grant lock is released (DL-108).
        var item = await PlannedExistingAsync();
        var (planned, _) = await PlannedAsync(item);
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var probe = new StepProbeTarget(onRun: step => Task.FromResult(step.Action == ProvisioningPlanner.EntitlementAdd
            ? new StepResult(StepStatus.Unknown, StepCodes.LedgerDamaged)
            : new StepResult(StepStatus.Done, ProvisioningPlanner.Applied)));
        (await RunAsync(item, probe, clock)).ShouldBeTrue();
        await using (var context = ProvisioningContext())
        {
            var row = await context.GrantLocks.AsNoTracking().SingleAsync(l => l.AccountId == planned.AccountId && l.EntitlementId == planned.EntitlementId, Ct);
            row.UntilUtc.ShouldBeLessThan(clock.GetUtcNow(), "the lock was released");
        }

        probe.Reads.ShouldBe(0, "no read settles a damaged record");
        probe.Removes.ShouldBe(0, "the damaged step itself is never undone");
        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe(ProvisioningRunner.LedgerDamaged));
        steps.Single(s => s.Action == ProvisioningPlanner.EntitlementAdd).ResultCode.ShouldBe(ProvisioningRunner.LedgerDamaged);
    }

    [Fact]
    public async Task ADamagedRecordOfAnUndoStepFailsTheUndoForAPerson()
    {
        // The add applies but the final read doesn't match, so the undo removes it; the removal's stored record is damaged. It
        // is not retried or settled by a read (which would match here): the undo fails for a person.
        var item = await PlannedExistingAsync();
        var probe = new StepProbeTarget(
            onRun: step => Task.FromResult(step.Action == ProvisioningPlanner.EntitlementRemove
                ? new StepResult(StepStatus.Unknown, StepCodes.LedgerDamaged)
                : new StepResult(StepStatus.Done, ProvisioningPlanner.Applied)),
            onRead: step => Task.FromResult<bool?>(step.Action == ProvisioningPlanner.EntitlementRemove));
        (await RunAsync(item, probe)).ShouldBeTrue();

        probe.Removes.ShouldBe(1);
        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe("UndoFailed"));
        steps.Single(s => s.Action == ProvisioningPlanner.EntitlementRemove).ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(ProvisioningState.Failed),
            s => s.ResultCode.ShouldBe(ProvisioningRunner.LedgerDamaged));
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

        // It stopped at the renewal, after the add was recorded as done, not earlier for some other reason; the other run still
        // holds the lock.
        await using var audit = TestSupport.CreateAudit(db.Provider, db.AuditAppConnectionString);
        (await new AuditTrail(audit).ForTargetAsync(ProvisioningAuditActions.Target, item.ToString(), 100, Ct))
            .ShouldContain(e => e.Action == ProvisioningAuditActions.StepDone);
        await using var context = ProvisioningContext();
        var row = await context.GrantLocks.AsNoTracking().SingleAsync(l => l.AccountId == planned.AccountId && l.EntitlementId == planned.EntitlementId, Ct);
        row.HeldBy.ShouldNotBe(item, "the other run still holds the lock");
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

    [Fact]
    [SuppressMessage("Security", "CA2100", Justification = "Test: SQL text is constant; no user input.")]
    public async Task TheUpgradeStopsWhenAFailedOperationIsStored()
    {
        // DL-106's upgrade check, run exactly as the migration runs it, against a stored failure. The row can only be put there
        // with the check turned off, inside a transaction that is never committed: nothing is left behind.
        await using var connection = db.OpenConnection(db.MigratorConnectionString);
        await using var transaction = await connection.BeginTransactionAsync(Ct);

        async Task ExecuteAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(Ct);
        }

        if (IsSqlServer)
        {
            await ExecuteAsync("ALTER TABLE [provisioning].[OperationLedger] NOCHECK CONSTRAINT [CK_OperationLedger_Values];");
            await ExecuteAsync("INSERT INTO [provisioning].[OperationLedger] ([OperationId], [Fingerprint], [Status], [Code], [RecordedAtUtc]) VALUES ('0190a3f0-0000-7000-8000-00000000d107', REPLICATE(N'c', 64), N'Failed', N'RateLimited', SYSDATETIMEOFFSET());");
            var stopped = await Should.ThrowAsync<SqlException>(() => ExecuteAsync(IAM.Persistence.SqlServer.Migrations.Provisioning.LedgerNoFailed.Check));
            stopped.Number.ShouldBe(51002);
        }
        else
        {
            await ExecuteAsync("ALTER TABLE provisioning.operation_ledger DROP CONSTRAINT ck_operation_ledger_values;");
            await ExecuteAsync("INSERT INTO provisioning.operation_ledger (operation_id, fingerprint, status, code, recorded_at_utc) VALUES ('0190a3f0-0000-7000-8000-00000000d107', repeat('c', 64), 'Failed', 'RateLimited', now());");
            var stopped = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(IAM.Persistence.PostgreSql.Migrations.Provisioning.LedgerNoFailed.Check));
            stopped.ShouldSatisfyAllConditions(e => e.SqlState.ShouldBe("P0001"), e => e.MessageText.ShouldContain("DL-106"));
        }
    }

    [Fact]
    public async Task ALedgerRowForACreateThatWasNeverSentIsDamageNotACreatedAccount()
    {
        // DL-109: a well-formed row, with the right fingerprint, that no call to this directory wrote (here: the same create run on
        // another directory that shares the ledger, as anyone who can write the ledger could forge it). The SDK replays it, but
        // the create was never sent before, so the runner takes it as damage: nothing is taken as the item's account.
        var (item, name, _, group) = await PlannedNewAccountAsync();
        var (_, planned) = await PlannedAsync(item);
        var ledger = new InMemoryOperationLedger();
        var elsewhere = SharedLedgerMock(group, ledger);
        (await new ConnectorTarget(elsewhere).RunAsync(StepOf(planned.Single(s => s.Step == 1)), Ct)).Status.ShouldBe(StepStatus.Done);
        var mock = SharedLedgerMock(group, ledger);

        (await RunAsync(item, new ConnectorTarget(mock))).ShouldBeTrue();

        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe(ProvisioningRunner.LedgerDamaged));
        steps.Single(s => s.Step == 1).ResultCode.ShouldBe(ProvisioningRunner.LedgerDamaged);
        steps.ShouldNotContain(s => s.Step == 2 && s.State != ProvisioningState.Planned, "nothing after the create ran");
        (await mock.ReadAsync(new ObjectRef(ObjectTypes.Account, $"CN={name},{AccountContainer}"), Ct)).ShouldBeNull("nothing was written here");
        (await FinalAuditAsync(item)).ShouldSatisfyAllConditions(p => p["step"].ShouldBe("1"), p => p["code"].ShouldBe(ProvisioningRunner.LedgerDamaged));
    }

    [Fact]
    public async Task AReplayOfAnAddThatWasNeverSentIsDamage()
    {
        // DL-109: on an existing account, the add's first attempt is answered from a ledger: no call ever sent it, so the row is
        // not this item's change. The item fails for a person and nothing is undone.
        var item = await PlannedExistingAsync();
        var probe = new StepProbeTarget(onRun: _ => Task.FromResult(new StepResult(StepStatus.Done, ProvisioningPlanner.Applied) { Replayed = true }));
        (await RunAsync(item, probe, new MovingClock(DateTimeOffset.UtcNow))).ShouldBeTrue();

        probe.Reads.ShouldBe(0);
        probe.Removes.ShouldBe(0);
        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe(ProvisioningRunner.LedgerDamaged));
        steps.Single(s => s.Action == ProvisioningPlanner.EntitlementAdd).ResultCode.ShouldBe(ProvisioningRunner.LedgerDamaged);
    }

    [Theory]
    [InlineData(StepStatus.Retry)]
    [InlineData(StepStatus.Unknown)]
    public async Task AReplayOfTheAddThatWaitedLastTimeIsTaken(StepStatus first)
    {
        // DL-109: the add waited (failed for now, or its outcome unknown); the next run's attempt is answered from the ledger.
        // That step was sent before, so the replay is its own earlier change and the item succeeds.
        var item = await PlannedExistingAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var calls = 0;
        var probe = new StepProbeTarget(
            onRun: _ => Task.FromResult(++calls == 1
                ? new StepResult(first, ResultCodes.Throttled)
                : new StepResult(StepStatus.Done, ProvisioningPlanner.Applied) { Replayed = true }),
            onRead: _ => Task.FromResult<bool?>(calls == 1 ? null : true));
        (await RunAsync(item, probe, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Planned, "the add waits");

        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, probe, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Succeeded);
    }

    [Fact]
    public async Task AReplayOfALaterStepThatWaitedLastTimeIsTaken()
    {
        // DL-109: on a new account the enable (step 3, not one the ledger settles) broke off and a read couldn't confirm it, so it
        // waited. The next run reaches it first, and its answer from the ledger is its own earlier change: the item succeeds.
        var (item, _, _, _) = await PlannedNewAccountAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var enables = 0;
        var probe = new StepProbeTarget(
            onRun: step => Task.FromResult(step.Action != ProvisioningPlanner.AccountEnable
                ? new StepResult(StepStatus.Done, ProvisioningPlanner.Applied)
                : ++enables == 1
                    ? new StepResult(StepStatus.Unknown, ResultCodes.TimedOut)
                    : new StepResult(StepStatus.Done, ProvisioningPlanner.Applied) { Replayed = true }),
            onRead: step => Task.FromResult<bool?>(step.Action == ProvisioningPlanner.AccountEnable && enables == 1 ? false : true));
        (await RunAsync(item, probe, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Planned, "the enable waits");

        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, probe, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Succeeded);
    }

    [Fact]
    public async Task AReplayOfAStepAfterTheOneThatWaitedIsDamage()
    {
        // DL-109: on a new account the create waited; the next run's create succeeds, and the add after it, never sent before, is
        // answered from a ledger. Only the first step a later run reaches may be its own earlier change: this is damage.
        var (item, _, _, _) = await PlannedNewAccountAsync();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var creates = 0;
        var probe = new StepProbeTarget(onRun: step => Task.FromResult(step.Action switch
        {
            ProvisioningPlanner.AccountCreate => ++creates == 1
                ? new StepResult(StepStatus.Retry, ResultCodes.Throttled)
                : new StepResult(StepStatus.Done, ProvisioningPlanner.Applied),
            ProvisioningPlanner.EntitlementAdd => new StepResult(StepStatus.Done, ProvisioningPlanner.Applied) { Replayed = true },
            _ => new StepResult(StepStatus.Done, ProvisioningPlanner.Applied),
        }));
        (await RunAsync(item, probe, clock)).ShouldBeTrue();
        (await PlannedAsync(item)).Item.State.ShouldBe(ProvisioningState.Planned, "the create waits");

        clock.Advance(TimeSpan.FromHours(1));
        (await RunAsync(item, probe, clock)).ShouldBeTrue();
        var (failed, steps) = await PlannedAsync(item);
        failed.ShouldSatisfyAllConditions(i => i.State.ShouldBe(ProvisioningState.Failed), i => i.Problem.ShouldBe(ProvisioningRunner.LedgerDamaged));
        steps.Single(s => s.Step == 2).ResultCode.ShouldBe(ProvisioningRunner.LedgerDamaged);
    }

    /// <summary>A mock directory over a ledger it shares with others (DL-109).</summary>
    private static MockActiveDirectoryConnector SharedLedgerMock(string group, IOperationLedger ledger)
    {
        var mock = new MockActiveDirectoryConnector(ConnectorSettings.Default, ledger, "DC=corp,DC=test");
        mock.AddContainer("Users");
        mock.AddGroup(group["CN=".Length..group.IndexOf(',', StringComparison.Ordinal)], mock.AddContainer("Groups"));
        return mock;
    }

    /// <summary>A stored step as the runner hands it to a target.</summary>
    private static TargetStep StepOf(ProvisioningOperation o) => new(
        o.Id, o.Action, o.TargetType, o.TargetId, o.RelatedType, o.RelatedId,
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(o.Attributes) ?? []);

    /// <summary>A mock directory over the durable ledger, whose stored record of one operation is damaged (DL-108).</summary>
    private static MockActiveDirectoryConnector DurableMock(string group, Guid damaged, out ServiceProvider services)
    {
        services = new ServiceCollection().AddSingleton<IProvisioningLedger>(new SelectiveLedger(damaged)).BuildServiceProvider();
        var mock = new MockActiveDirectoryConnector(
            ConnectorSettings.Default, new DurableOperationLedger(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System), "DC=corp,DC=test");
        mock.AddContainer("Users");
        mock.AddGroup(group["CN=".Length..group.IndexOf(',', StringComparison.Ordinal)], mock.AddContainer("Groups"));
        return mock;
    }

    /// <summary>The payload of the item's one Failed audit record; no step was retried with the damage code.</summary>
    private async Task<IReadOnlyDictionary<string, string?>> FinalAuditAsync(Guid item)
    {
        await using var audit = TestSupport.CreateAudit(db.Provider, db.AuditAppConnectionString);
        var trail = await new AuditTrail(audit).ForTargetAsync(ProvisioningAuditActions.Target, item.ToString(), 100, Ct);
        trail.ShouldNotContain(a => a.Action == ProvisioningAuditActions.StepRetry && a.Payload.ContainsKey("code") && a.Payload["code"] == ProvisioningRunner.LedgerDamaged,
            "a damaged step is never retried");
        return trail.Single(a => a.Action == ProvisioningAuditActions.Failed).Payload;
    }

    /// <summary>The check that refuses a failed ledger row, by its own error: not any database error (DL-107).</summary>
    private void ShouldBeLedgerValuesCheck(Exception exception)
    {
        switch (exception.GetBaseException())
        {
            case SqlException sql when IsSqlServer:
                sql.ShouldSatisfyAllConditions(e => e.Number.ShouldBe(547), e => e.Message.ShouldContain("CK_OperationLedger_Values"));
                break;
            case PostgresException postgres when !IsSqlServer:
                postgres.ShouldSatisfyAllConditions(e => e.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation), e => e.ConstraintName.ShouldBe("ck_operation_ledger_values"));
                break;
            default:
                throw new ShouldAssertException($"Expected the ledger's values check, got {exception.GetBaseException().GetType().Name}.");
        }
    }

    /// <summary>A durable ledger store that holds one stored failure for every operation: a record the SDK never writes.</summary>
    private sealed class DamagedLedger : IProvisioningLedger
    {
        public Task<OperationLedgerRecord?> FindAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult<OperationLedgerRecord?>(OperationLedgerRecord.Create(operationId, new string('c', 64), nameof(OperationStatus.Failed), ResultCodes.RateLimited, DateTimeOffset.UtcNow));

        public Task RecordAsync(OperationLedgerRecord record, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Nothing is recorded after a damaged record.");
    }

    /// <summary>A durable ledger store that keeps what is recorded, but holds a stored failure for one operation (DL-108).</summary>
    private sealed class SelectiveLedger(Guid damaged) : IProvisioningLedger
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, OperationLedgerRecord> _records = new();

        public Task<OperationLedgerRecord?> FindAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(operationId == damaged
                ? OperationLedgerRecord.Create(operationId, new string('c', 64), nameof(OperationStatus.Failed), ResultCodes.RateLimited, DateTimeOffset.UtcNow)
                : _records.GetValueOrDefault(operationId));

        public Task RecordAsync(OperationLedgerRecord record, CancellationToken cancellationToken)
        {
            _records.TryAdd(record.OperationId, record);
            return Task.CompletedTask;
        }
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
