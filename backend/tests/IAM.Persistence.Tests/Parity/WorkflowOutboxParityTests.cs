using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Catalog.Infrastructure.Applications;
using IAM.Modules.Organization.Infrastructure.Workgroups;
using IAM.Modules.Workflow.Application.Approvals;
using IAM.Modules.Workflow.Application.Approvers;
using IAM.Modules.Workflow.Contracts;
using IAM.Modules.Workflow.Domain.Approvals;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// The workflow outbox on SQL Server and PostgreSQL (DL-076): each saved action writes exactly the events it caused, in
/// its own transaction; refusals, no-op timeouts and lost races write nothing; payloads carry no reasons; and the
/// database keeps what a message says fixed while letting its delivery fields move forward.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class WorkflowOutboxParityTests(ParityDatabase db)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly PersonId Requester = PersonId.Create("wfo-requester");
    private static readonly PersonId Beneficiary = PersonId.Create("wfo-beneficiary");
    private static readonly PersonId A = PersonId.Create("wfo-a");
    private static readonly PersonId B = PersonId.Create("wfo-b");
    private static readonly PersonId C = PersonId.Create("wfo-c");
    private const string Escalator = "wfo-escalator";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    [Fact]
    public async Task AnApprovedRequestPublishesStartTasksAuthorisationAndClosure()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, A), Level(2, B)), Ct);
        // Events of one save share its time; their order among themselves isn't promised (consumers order by EngineVersion).
        var first = await MessagesAsync(id);
        first.Select(m => m.Type).ShouldBe(["workflow.approval.started", "workflow.approval.task_opened"], ignoreOrder: true);
        Read<ApprovalStarted>(first).ShouldBe(new ApprovalStarted(id, started.RequestItemId, Requester.Value, Beneficiary.Value, started.Version));
        var level1 = Read<ApprovalTaskOpened>(first);
        level1.StageId.ShouldBe(started.Stages[0].StageId);
        level1.People.ShouldBe([A.Value]);
        level1.DeadlineUtc.ShouldBe(T0.AddHours(4));

        clock.Advance(TimeSpan.FromMinutes(1));
        var afterA = await Service(A.Value, clock).DecideAsync(id, started.Version, Guid.CreateVersion7(), started.Stages[0].StageId, true, null, Ct);
        var second = (await MessagesAsync(id)).Skip(2).ToList();
        second.Select(m => m.Type).ShouldBe(["workflow.approval.task_opened"]);
        Read<ApprovalTaskOpened>(second).People.ShouldBe([B.Value]);

        clock.Advance(TimeSpan.FromMinutes(1));
        var done = await Service(B.Value, clock).DecideAsync(id, afterA.Version, Guid.CreateVersion7(), afterA.Stages[1].StageId, true, "Looks fine", Ct);
        var last = (await MessagesAsync(id)).Skip(3).ToList();
        last.Select(m => m.Type).ShouldBe(["workflow.approval.authorized", "workflow.approval.closed"], ignoreOrder: true);
        Read<ApprovalAuthorized>(last).EngineVersion.ShouldBe(done.Version);
        Read<ApprovalClosed>(last).ShouldBe(new ApprovalClosed(id, done.RequestItemId, "Approved", true, done.Version));

        var all = await MessagesAsync(id);
        all.Select(m => m.IdempotencyKey).Distinct().Count().ShouldBe(all.Count);
        all.ShouldAllBe(m => m.Source == "workflow" && m.SchemaVersion == 1 && m.OccurredAtUtc >= T0);
        all.ShouldAllBe(m => !m.Payload.Contains("Looks fine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParallelLevelsEachPublishOnceAndADecisionRepublishesNobody()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, A), Level(1, B)), Ct);
        var opened = (await MessagesAsync(id)).Where(m => m.Type == ApprovalTaskOpened.MessageType).Select(MessageEnvelopes.Read<ApprovalTaskOpened>).ToList();
        opened.Select(o => o.StageId).ShouldBe(started.Stages.Select(s => s.StageId), ignoreOrder: true);

        // A decides on their level; B could act before and still can, so nobody is announced again.
        clock.Advance(TimeSpan.FromMinutes(1));
        var stageA = started.Stages.Single(s => s.Candidates.Contains(A.Value)).StageId;
        (await Service(A.Value, clock).DecideAsync(id, started.Version, Guid.CreateVersion7(), stageA, true, null, Ct)).State.ShouldBe("InProgress");
        (await MessagesAsync(id)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task AuthorisationIsPublishedOnceEvenWhenActionLevelsFollow()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        var task = Level(2, B) with { Kind = StageKind.Action };
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, A), task), Ct);

        clock.Advance(TimeSpan.FromMinutes(1));
        var authorised = await Service(A.Value, clock).DecideAsync(id, started.Version, Guid.CreateVersion7(), started.Stages[0].StageId, true, null, Ct);
        authorised.ProvisioningAuthorized.ShouldBeTrue();
        var afterDecision = (await MessagesAsync(id)).Skip(2).ToList();
        afterDecision.Select(m => m.Type).ShouldBe([ApprovalAuthorized.MessageType, ApprovalTaskOpened.MessageType], ignoreOrder: true);
        Read<ApprovalTaskOpened>(afterDecision).Kind.ShouldBe("Action");

        clock.Advance(TimeSpan.FromMinutes(1));
        var done = await Service(B.Value, clock).CompleteAsync(id, authorised.Version, Guid.CreateVersion7(), task.StageId, null, Ct);
        done.State.ShouldBe("Approved");
        (await MessagesAsync(id)).Skip(4).Select(m => m.Type).ShouldBe([ApprovalClosed.MessageType]);
    }

    [Fact]
    public async Task ARejectionClosesWithoutAuthorisationOrItsReason()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, A)), Ct);
        await Service(A.Value, clock).DecideAsync(id, started.Version, Guid.CreateVersion7(), started.Stages[0].StageId, false, "Secret reason for rejecting", Ct);
        var messages = await MessagesAsync(id);
        messages.Select(m => m.Type).ShouldBe(["workflow.approval.started", "workflow.approval.task_opened", "workflow.approval.closed"], ignoreOrder: true);
        Read<ApprovalClosed>(messages).Outcome.ShouldBe("Rejected");
        messages.ShouldAllBe(m => !m.Payload.Contains("Secret reason", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefusalsNoOpTimeoutsAndLostRacesWriteNothing()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, A, B)), Ct);
        var count = (await MessagesAsync(id)).Count;

        await Should.ThrowAsync<ApprovalRefusedException>(() => Service(A.Value, clock).DecideAsync(id, started.Version - 1, Guid.CreateVersion7(), started.Stages[0].StageId, true, null, Ct));
        await Service(null, clock).ApplyTimeoutsAsync(id, started.Version, Ct);
        (await MessagesAsync(id)).Count.ShouldBe(count, "a refusal and a timeout that changed nothing publish nothing");

        // Both decide on the same version: only the saved one publishes.
        var stage = started.Stages[0].StageId;
        await Task.WhenAll(
            Swallow(() => Service(A.Value, clock).DecideAsync(id, started.Version, Guid.CreateVersion7(), stage, true, null, Ct)),
            Swallow(() => Service(B.Value, clock).DecideAsync(id, started.Version, Guid.CreateVersion7(), stage, true, null, Ct)));
        var messages = await MessagesAsync(id);
        messages.Count(m => m.Type == "workflow.approval.authorized").ShouldBe(1);
        messages.Count(m => m.Type == "workflow.approval.closed").ShouldBe(1);
    }

    [Fact]
    public async Task ALargeLevelIsAnnouncedInChunksThatFitThePayloadLimit()
    {
        // DL-079: 70 approvers with 150-character ids that JSON escapes six-fold: one event would exceed 64 KB.
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        var people = Enumerable.Range(0, 70).Select(i => PersonId.Create($"wfo-{i:D2}-" + new string('é', 150))).ToArray();
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, people)), Ct);

        var opened = (await MessagesAsync(id)).Where(m => m.Type == ApprovalTaskOpened.MessageType).ToList();
        opened.Count.ShouldBe(3);
        opened.ShouldAllBe(m => m.Payload.Length <= MessageEnvelopes.MaxPayloadLength);
        var events = opened.Select(MessageEnvelopes.Read<ApprovalTaskOpened>).ToList();
        events.ShouldAllBe(e => e.People.Count <= ApprovalTaskOpened.MaxPeople && e.StageId == started.Stages[0].StageId);
        events.SelectMany(e => e.People).ShouldBe(people.Select(p => p.Value), ignoreOrder: true);
        opened.Select(m => m.IdempotencyKey).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task AnEscalationPublishesOnlyTheNewPeople()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();

        // The only named approver is the requester, so the level holds with nobody to act.
        var started = await Service(null, clock).StartAsync(Binding(id), Route(Level(1, Requester)), Ct);
        (await MessagesAsync(id)).Select(m => m.Type).ShouldBe(["workflow.approval.started"]);

        var escalated = await Service(Escalator, clock).EscalateAsync(id, started.Version, started.Stages[0].StageId, [A, C], "Owner away", Ct);
        var opened = (await MessagesAsync(id)).Single(m => m.Type == ApprovalTaskOpened.MessageType);
        MessageEnvelopes.Read<ApprovalTaskOpened>(opened).People.ShouldBe([A.Value, C.Value]);
        MessageEnvelopes.Read<ApprovalTaskOpened>(opened).EngineVersion.ShouldBe(escalated.Version);
    }

    [Fact]
    public async Task TheDatabaseKeepsAMessageAsWrittenAndItsDeliveryMovingForward()
    {
        var clock = new MovingClock(T0);
        var id = Guid.CreateVersion7();
        await Service(null, clock).StartAsync(Binding(id), Route(Level(1, A)), Ct);
        var message = (await MessagesAsync(id)).Single(m => m.Type == ApprovalStarted.MessageType).MessageId;
        var where = $"WHERE {Col("MessageId")} = '{message}'";

        // Delivery fields may move forward.
        await db.ExecuteAsync(db.WorkflowAppConnectionString, $"UPDATE {Table()} SET {Col("Attempts")} = 2, {Col("LastError")} = 'BrokerUnavailable' {where}", Ct);
        await db.ExecuteAsync(db.WorkflowAppConnectionString, $"UPDATE {Table()} SET {Col("DispatchedAtUtc")} = '2026-11-15T09:05:00+00:00', {Col("Attempts")} = 3 {where}", Ct);

        string[] refused =
        [
            $"UPDATE {Table()} SET {Col("Payload")} = '{{\"instanceId\":null}}' {where}",
            $"UPDATE {Table()} SET {Col("Type")} = 'workflow.approval.authorized' {where}",
            $"UPDATE {Table()} SET {Col("IdempotencyKey")} = 'other' {where}",
            $"UPDATE {Table()} SET {Col("CorrelationId")} = 'other' {where}",
            $"UPDATE {Table()} SET {Col("CausationId")} = NULL {where}",
            $"UPDATE {Table()} SET {Col("OccurredAtUtc")} = '2026-11-15T10:00:00+00:00' {where}",
            $"UPDATE {Table()} SET {Col("SchemaVersion")} = 2 {where}",
            $"UPDATE {Table()} SET {Col("DispatchedAtUtc")} = NULL {where}",
            $"UPDATE {Table()} SET {Col("DispatchedAtUtc")} = '2026-11-15T09:06:00+00:00' {where}",
            $"UPDATE {Table()} SET {Col("Attempts")} = 1 {where}",
            $"UPDATE {Table()} SET {Col("Attempts")} = -1 {where}",
            $"DELETE FROM {Table()} {where}",
        ];
        foreach (var sql in refused)
        {
            await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.WorkflowAppConnectionString, sql, Ct), sql);
        }

        if (!IsSqlServer)
        {
            await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, "TRUNCATE workflow.outbox_message", Ct));
        }

        (await MessagesAsync(id)).Single(m => m.MessageId == message).Type.ShouldBe(ApprovalStarted.MessageType);
    }

    private async Task<List<MessageEnvelope>> MessagesAsync(Guid instanceId)
    {
        await using var context = TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString);
        var prefix = $"workflow.action:{instanceId:N}:";
        var rows = await context.Outbox.AsNoTracking().Where(m => m.IdempotencyKey.StartsWith(prefix)).ToListAsync(Ct);
        return [.. rows.OrderBy(m => m.OccurredAtUtc).ThenBy(m => m.MessageId).Select(m => m.ToEnvelope())];
    }

    private static T Read<T>(IEnumerable<MessageEnvelope> messages)
        where T : IAM.BuildingBlocks.Abstractions.Messaging.IIntegrationEvent =>
        MessageEnvelopes.Read<T>(messages.Single(m => m.Type == T.MessageType));

    private static async Task Swallow(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception ex) when (WorkflowApi.Classify(ex, out _) is WorkflowFailureKind.Conflict or WorkflowFailureKind.WrongState)
        {
        }
    }

    private static ApprovalBinding Binding(Guid id) =>
        new(id, Guid.CreateVersion7(), Guid.CreateVersion7(), new string('e', 64), Guid.CreateVersion7(), Guid.CreateVersion7(), Requester, Beneficiary, PersonId.Create("wfo-policy-maker"));

    private static StageDefinition Level(int sequence, params PersonId[] people) =>
        new(Guid.CreateVersion7(), sequence, QuorumMode.AnyOne, null, false, false, true, TimeSpan.FromHours(4), TimeoutAction.Escalate, ApproverSource.Named(people));

    private static ApprovalRoute Route(params StageDefinition[] stages) => ApprovalRoute.Create(stages);

    private string Table() => IsSqlServer ? "[workflow].[OutboxMessage]" : "workflow.outbox_message";

    private string Col(string name) => IsSqlServer ? $"[{name}]" : IamModelConventions.ToSnakeCase(name);

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
        return new ApprovalWorkflowService(context, writer, clock, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), new EscalatorOnly(), resolver);
    }

    private sealed class EscalatorOnly : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) =>
            Task.FromResult(subject == Escalator && action == WorkflowAuditActions.EscalateAction);
    }

    private sealed class MovingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
