using System.Globalization;
using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Catalog.Infrastructure.Applications;
using IAM.Modules.Organization.Infrastructure.Workgroups;
using IAM.Modules.Workflow.Application.Approvals;
using IAM.Modules.Workflow.Application.Approvers;
using IAM.Modules.Workflow.Domain.Approvals;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using IAM.Modules.Workflow.Infrastructure.Persistence;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Durable approval workflows on SQL Server and PostgreSQL (DL-072): the action log replays to the stored history, every
/// action and refusal is audited, approvers are resolved when a level opens, tampering is detected, and the database
/// refuses changes to the logs by SQL.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class WorkflowParityTests(ParityDatabase db)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly PersonId Requester = PersonId.Create("wf-requester");
    private static readonly PersonId Beneficiary = PersonId.Create("wf-beneficiary");
    private static readonly PersonId PolicyMaker = PersonId.Create("wf-policy-maker");
    private static readonly PersonId A = PersonId.Create("wf-a");
    private static readonly PersonId B = PersonId.Create("wf-b");
    private static readonly PersonId C = PersonId.Create("wf-c");
    private const string Escalator = "wf-escalator";
    private const string Escalator2 = "wf-escalator-2";
    private const string Canceller = "wf-canceller";

    private static readonly FakePermissions Permissions = new(
        (Escalator, WorkflowAuditActions.EscalateAction), (Escalator2, WorkflowAuditActions.EscalateAction), (Canceller, WorkflowAuditActions.CancelAction));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    [Fact]
    public async Task TheMigrationAppliesOnce()
    {
        db.WorkflowFirstRunApplied.Count.ShouldBe(2);
        db.WorkflowFirstRunApplied[0].ShouldEndWith("_WorkflowStorage");
        db.WorkflowFirstRunApplied[1].ShouldEndWith("_WorkflowTasks");
        db.WorkflowSecondRunPending.ShouldBeEmpty();
        (await AuditCountAsync(Guid.NewGuid(), WorkflowAuditActions.Refused, "Denied")).ShouldBe(0);
    }

    [Fact]
    public async Task ARequestRunsToApprovalAndEveryStepIsStoredAndAudited()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, A), Level(2, B)), Ct);
        started.State.ShouldBe("InProgress");
        started.Stages[0].State.ShouldBe("Active");
        started.Stages[0].Candidates.ShouldBe([A.Value]);
        started.Stages[1].State.ShouldBe("Pending");

        clock.Advance(TimeSpan.FromMinutes(5));
        var afterA = await Service(A.Value, clock).DecideAsync(id, started.Version, Guid.CreateVersion7(), started.Stages[0].StageId, null, true, null, Ct);
        afterA.Stages[1].State.ShouldBe("Active");
        afterA.Stages[1].Candidates.ShouldBe([B.Value]);

        clock.Advance(TimeSpan.FromMinutes(5));
        var done = await Service(B.Value, clock).DecideAsync(id, afterA.Version, Guid.CreateVersion7(), afterA.Stages[1].StageId, null, true, null, Ct);
        done.State.ShouldBe("Approved");
        done.ProvisioningAuthorized.ShouldBeTrue();

        // A fresh load replays the log to the same instance.
        var found = (await Service(null, clock).FindAsync(id, Ct)).ShouldNotBeNull();
        found.Oversight.ShouldNotBeNull().History.Count.ShouldBe((int)done.Version);
        (found with { Oversight = null }).ShouldBeEquivalentTo(done);
        (await CountAsync("ApprovalAction", id)).ShouldBe(5, "bound, supplied, decided, supplied, decided");
        (await CountAsync("ApprovalHistory", id)).ShouldBe(done.Version);
        (await AuditCountAsync(id, WorkflowAuditActions.For(ApprovalActionKind.Bound), "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(id, WorkflowAuditActions.For(ApprovalActionKind.ApproversSupplied), "Succeeded")).ShouldBe(2);
        (await AuditCountAsync(id, WorkflowAuditActions.For(ApprovalActionKind.Decided), "Succeeded")).ShouldBe(2);
    }

    [Fact]
    public async Task ARefusedActionChangesNothingAndIsAudited()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, A)), Ct);
        var stage = started.Stages[0].StageId;

        // Someone the request doesn't concern can't tell it exists (DL-073c); the refusal is still audited.
        await Should.ThrowAsync<ApprovalInstanceNotFoundException>(() => Service(C.Value, clock).DecideAsync(id, started.Version, Guid.CreateVersion7(), stage, null, true, null, Ct));
        (await Should.ThrowAsync<ApprovalRefusedException>(() => Service(A.Value, clock).DecideAsync(id, started.Version - 1, Guid.CreateVersion7(), stage, null, true, null, Ct)))
            .Refusal.ShouldBe(ApprovalRefusal.StaleVersion);
        (await Should.ThrowAsync<ApprovalRefusedException>(() => Service(null, clock).DecideAsync(id, started.Version, Guid.CreateVersion7(), stage, null, true, null, Ct)))
            .Refusal.ShouldBe(ApprovalRefusal.InvalidActor);
        await Should.ThrowAsync<ApprovalInstanceNotFoundException>(() => Service(A.Value, clock).DecideAsync(Guid.CreateVersion7(), 1, Guid.CreateVersion7(), stage, null, true, null, Ct));

        (await CountAsync("ApprovalAction", id)).ShouldBe(2);
        (await AuditCountAsync(id, WorkflowAuditActions.Refused, "Denied")).ShouldBe(3);
        (await Service(null, clock).FindAsync(id, Ct)).ShouldNotBeNull().Version.ShouldBe(started.Version);
    }

    [Fact]
    public async Task EscalationAndCancellingNeedTheirPermissions()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();

        // The only named approver is the requester, so the level finds nobody, the empty fallback group too, and it holds.
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, Requester)), Ct);
        started.State.ShouldBe("Held");
        var stage = started.Stages[0].StageId;

        await Should.ThrowAsync<ApprovalInstanceNotFoundException>(() => Service(Canceller, clock).EscalateAsync(id, started.Version, stage, [A], "Owner away", Ct));
        (await Should.ThrowAsync<ApprovalRefusedException>(() => Service(Requester.Value, clock).EscalateAsync(id, started.Version, stage, [A], "Owner away", Ct)))
            .Refusal.ShouldBe(ApprovalRefusal.EscalatePermissionMissing);
        var escalated = await Service(Escalator, clock).EscalateAsync(id, started.Version, stage, [A], "Owner away", Ct);
        escalated.Stages[0].Candidates.ShouldBe([A.Value]);

        (await Should.ThrowAsync<ApprovalRefusedException>(() => Service(Escalator, clock).CancelAsync(id, escalated.Version, "No longer needed", Ct)))
            .Refusal.ShouldBe(ApprovalRefusal.CancelPermissionMissing);
        (await Service(Canceller, clock).CancelAsync(id, escalated.Version, "No longer needed", Ct)).State.ShouldBe("Cancelled");
        (await AuditCountAsync(id, WorkflowAuditActions.Refused, "Denied")).ShouldBe(3);
        (await AuditCountAsync(id, WorkflowAuditActions.For(ApprovalActionKind.Escalated), "Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task AMandatoryLevelIsEscalatedByTwoPeople()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, Requester) with { Mandatory = true }), Ct);
        var stage = started.Stages[0].StageId;
        var proposed = await Service(Escalator, clock).ProposeEscalationAsync(id, started.Version, stage, [A], "Owner away", Ct);
        proposed.Stages[0].EscalationPending.ShouldBeTrue();
        (await Should.ThrowAsync<ApprovalRefusedException>(() => Service(Escalator, clock).ApproveEscalationAsync(id, proposed.Version, stage, [A], "Checked", Ct)))
            .Refusal.ShouldBe(ApprovalRefusal.ProposerCannotApproveEscalation);
        var approved = await Service(Escalator2, clock).ApproveEscalationAsync(id, proposed.Version, stage, [A], "Checked", Ct);
        approved.Stages[0].State.ShouldBe("Active");
        ((await Service(null, clock).FindAsync(id, Ct)).ShouldNotBeNull() with { Oversight = null }).ShouldBeEquivalentTo(approved);
    }

    [Fact]
    public async Task TimeoutsAreAppliedAtTheServersTime()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, A) with { OnTimeout = TimeoutAction.Reject }), Ct);
        (await NextDeadlineAsync(id)).ShouldBe(T0.AddHours(4));

        var early = await Service(null, clock).ApplyTimeoutsAsync(id, started.Version, Ct);
        early.State.ShouldBe("InProgress", "nothing has expired yet");
        clock.Advance(TimeSpan.FromHours(4));
        var late = await Service(null, clock).ApplyTimeoutsAsync(id, early.Version, Ct);
        late.State.ShouldBe("Rejected");
        (await NextDeadlineAsync(id)).ShouldBeNull();
    }

    [Fact]
    public async Task ARecordChangedBySqlIsRefusedAsDamaged()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, A)), Ct);

        // An extra history event the log doesn't produce: allowed by the triggers (no gap), caught by the replay.
        await db.ExecuteAsync(
            db.WorkflowAppConnectionString,
            $"INSERT INTO {Table("ApprovalHistory")} ({Col("InstanceId")}, {Col("EventVersion")}, {Col("AtUtc")}, {Col("Kind")}, {Col("StageId")}, {Col("Actor")}) VALUES ('{id}', {started.Version + 1}, '2026-11-14T09:00:00+00:00', 'InstanceApproved', NULL, NULL)",
            Ct);
        await Should.ThrowAsync<ApprovalInstanceDamagedException>(() => Service(null, clock).FindAsync(id, Ct));
        await Should.ThrowAsync<ApprovalInstanceDamagedException>(() => Service(A.Value, clock).DecideAsync(id, started.Version, Guid.CreateVersion7(), started.Stages[0].StageId, null, true, null, Ct));
        (await AuditCountAsync(id, WorkflowAuditActions.Damaged, "Denied")).ShouldBe(2);

        // A forged decision in the log: it replays, but not to the stored history.
        var forged = Guid.CreateVersion7();
        var other = await Service(null, clock).StartAsync(Binding(forged), Route(Level(1, A)), Ct);
        var decision = new ApprovalDecision(Guid.CreateVersion7(), other.Stages[0].StageId, A, null, true, null, other.Version);
        var payload = ApprovalActionLog.Decided(decision, T0.AddMinutes(1)).Payload.Replace("'", "''", StringComparison.Ordinal);
        await db.ExecuteAsync(
            db.WorkflowAppConnectionString,
            $"INSERT INTO {Table("ApprovalAction")} ({Col("InstanceId")}, {Col("Sequence")}, {Col("Kind")}, {Col("Payload")}, {Col("AtUtc")}, {Col("Actor")}, {Col("VersionAfter")}) VALUES ('{forged}', 3, 'Decided', '{payload}', '2026-11-14T09:01:00+00:00', 'wf-a', {other.Version + 3})",
            Ct);
        await Should.ThrowAsync<ApprovalInstanceDamagedException>(() => Service(null, clock).FindAsync(forged, Ct));

        // The summary moved forward with no action behind it: the triggers allow it, the replay doesn't.
        var bumped = Guid.CreateVersion7();
        await Service(null, clock).StartAsync(Binding(bumped), Route(Level(1, A)), Ct);
        await db.ExecuteAsync(
            db.WorkflowAppConnectionString,
            $"UPDATE {Table("ApprovalInstance")} SET {Col("ActionCount")} = {Col("ActionCount")} + 1 WHERE {Col("Id")} = '{bumped}'",
            Ct);
        await Should.ThrowAsync<ApprovalInstanceDamagedException>(() => Service(null, clock).FindAsync(bumped, Ct));
    }

    [Fact]
    public async Task TheDatabaseRefusesChangesToTheLogs()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, A)), Ct);
        string[] refused =
        [
            $"UPDATE {Table("ApprovalAction")} SET {Col("Payload")} = '{{}}' WHERE {Col("InstanceId")} = '{id}'",
            $"DELETE FROM {Table("ApprovalAction")} WHERE {Col("InstanceId")} = '{id}'",
            $"UPDATE {Table("ApprovalHistory")} SET {Col("Kind")} = 'InstanceApproved' WHERE {Col("InstanceId")} = '{id}'",
            $"DELETE FROM {Table("ApprovalHistory")} WHERE {Col("InstanceId")} = '{id}'",
            $"INSERT INTO {Table("ApprovalAction")} ({Col("InstanceId")}, {Col("Sequence")}, {Col("Kind")}, {Col("Payload")}, {Col("AtUtc")}, {Col("Actor")}, {Col("VersionAfter")}) VALUES ('{id}', 5, 'Decided', '{{}}', '2026-11-14T09:00:00+00:00', NULL, 99)",
            $"INSERT INTO {Table("ApprovalAction")} ({Col("InstanceId")}, {Col("Sequence")}, {Col("Kind")}, {Col("Payload")}, {Col("AtUtc")}, {Col("Actor")}, {Col("VersionAfter")}) VALUES ('{id}', 3, 'Decided', '{{}}', '2026-11-14T09:00:00+00:00', NULL, 1)",
            $"INSERT INTO {Table("ApprovalHistory")} ({Col("InstanceId")}, {Col("EventVersion")}, {Col("AtUtc")}, {Col("Kind")}, {Col("StageId")}, {Col("Actor")}) VALUES ('{id}', {started.Version + 2}, '2026-11-14T09:00:00+00:00', 'InstanceApproved', NULL, NULL)",
            $"UPDATE {Table("ApprovalInstance")} SET {Col("Requester")} = 'someone-else' WHERE {Col("Id")} = '{id}'",
            $"UPDATE {Table("ApprovalInstance")} SET {Col("ActionCount")} = 1 WHERE {Col("Id")} = '{id}'",
            $"UPDATE {Table("ApprovalInstance")} SET {Col("EngineVersion")} = 1, {Col("ActionCount")} = 9 WHERE {Col("Id")} = '{id}'",
            $"UPDATE {Table("ApprovalInstance")} SET {Col("State")} = 'Granted', {Col("ActionCount")} = 9 WHERE {Col("Id")} = '{id}'",
            $"DELETE FROM {Table("ApprovalInstance")} WHERE {Col("Id")} = '{id}'",
        ];
        foreach (var sql in refused)
        {
            await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.WorkflowAppConnectionString, sql, Ct), sql);
        }

        // A closed instance never changes state.
        await Service(Canceller, clock).CancelAsync(id, started.Version, "Not needed", Ct);
        await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(
            db.WorkflowAppConnectionString,
            $"UPDATE {Table("ApprovalInstance")} SET {Col("State")} = 'Approved', {Col("ActionCount")} = 99 WHERE {Col("Id")} = '{id}'",
            Ct));

        if (!IsSqlServer)
        {
            foreach (var table in new[] { "approval_instance", "approval_action", "approval_history" })
            {
                await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, $"TRUNCATE workflow.{table} CASCADE", Ct), table);
            }
        }

        (await Service(null, clock).FindAsync(id, Ct)).ShouldNotBeNull().State.ShouldBe("Cancelled");
    }

    private static ApprovalBinding Binding(Guid id) =>
        new(id, Guid.CreateVersion7(), Guid.CreateVersion7(), new string('a', 64), Guid.CreateVersion7(), Guid.CreateVersion7(), Requester, Beneficiary, PolicyMaker);

    private static StageDefinition Level(int sequence, params PersonId[] people) =>
        new(Guid.CreateVersion7(), sequence, QuorumMode.AnyOne, null, false, false, true, TimeSpan.FromHours(4), TimeoutAction.Escalate, ApproverSource.Named(people));

    private static ApprovalRoute Route(params StageDefinition[] stages) => ApprovalRoute.Create(stages);

    private string Table(string name) => IsSqlServer ? $"[workflow].[{name}]" : $"workflow.{IamModelConventions.ToSnakeCase(name)}";

    private string Col(string name) => IsSqlServer ? $"[{name}]" : IamModelConventions.ToSnakeCase(name);

    private async Task<long> CountAsync(string table, Guid instanceId) =>
        Convert.ToInt64(await db.ScalarAsync(db.WorkflowAppConnectionString, $"SELECT COUNT(*) FROM {Table(table)} WHERE {Col("InstanceId")} = '{instanceId}'", Ct), CultureInfo.InvariantCulture);

    private async Task<DateTimeOffset?> NextDeadlineAsync(Guid instanceId)
    {
        var value = await db.ScalarAsync(db.WorkflowAppConnectionString, $"SELECT {Col("NextDeadlineUtc")} FROM {Table("ApprovalInstance")} WHERE {Col("Id")} = '{instanceId}'", Ct);
        return value switch
        {
            DateTimeOffset offset => offset.ToUniversalTime(),
            DateTime time => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)),
            _ => null,
        };
    }

    private async Task<long> AuditCountAsync(Guid targetId, string action, string outcome)
    {
        var sql = IsSqlServer
            ? $"SELECT COUNT(*) FROM [audit].[AuditEvent] WHERE [TargetId] = '{targetId}' AND [Action] = '{action}' AND [Outcome] = '{outcome}'"
            : $"SELECT COUNT(*) FROM audit.audit_event WHERE target_id = '{targetId}' AND action = '{action}' AND outcome = '{outcome}'";
        return Convert.ToInt64(await db.ScalarAsync(db.AuditAppConnectionString, sql, Ct), CultureInfo.InvariantCulture);
    }

    private ApprovalWorkflowService Service(string? actor, TimeProvider clock)
    {
        var context = TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString, new AuditingSaveChangesInterceptor(clock, new FixedActor(actor ?? "system:test")));
        var writer = new AuditWriter(
            TestSupport.ProviderFor(db.Provider),
            clock,
            new FixedActor(actor ?? "system:test"),
            new AuditActionCatalog(WorkflowAuditActions.All.Select(a => new AuditActionRegistration(a))));
        var resolver = new ApproverResolver(
            new ApplicationApproverDirectory(TestSupport.CreateCatalog(db.Provider, db.CatalogAppConnectionString)),
            new WorkgroupDirectory(TestSupport.CreateOrganization(db.Provider, db.OrganizationAppConnectionString)),
            new ApproverResolutionSettings());
        return new ApprovalWorkflowService(context, writer, clock, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), Permissions, resolver);
    }

    private sealed class FakePermissions(params (string Person, string Action)[] grants) : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) =>
            Task.FromResult(grants.Contains((subject, action)));
    }

    private sealed class MovingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
