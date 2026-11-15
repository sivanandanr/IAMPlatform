using System.Globalization;
using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Messaging.RabbitMq;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Catalog.Infrastructure.Applications;
using IAM.Modules.Organization.Infrastructure.Workgroups;
using IAM.Modules.Workflow.Application.Approvers;
using IAM.Modules.Workflow.Domain.Approvals;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using IAM.Workers.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// The workflow worker (DL-074) on both engines: due instances get their timeouts at the server's time, others are left
/// alone, two sweeps at once apply each timeout once, and a damaged instance is set aside after one audit.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class WorkflowTimeoutWorkerParityTests(ParityDatabase db)
{
    private static readonly PersonId Requester = PersonId.Create("wft-requester");
    private static readonly PersonId A = PersonId.Create("wft-a");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    [Fact]
    public async Task DueInstancesAreTimedOutAndOthersAreLeftAlone()
    {
        var now = DateTimeOffset.UtcNow;
        var rejecting = await StartAsync(now.AddHours(-10), Level(A, TimeoutAction.Reject));
        var cancelling = await StartAsync(now.AddHours(-10), Level(A, TimeoutAction.Cancel));
        var holding = await StartAsync(now.AddHours(-10), Level(A, TimeoutAction.Escalate));
        var notDue = await StartAsync(now, Level(A, TimeoutAction.Reject));

        using var host = Worker(batchSize: 50);
        using (var scope = host.Services.CreateScope())
        {
            var due = await scope.ServiceProvider.GetRequiredService<IAM.Modules.Workflow.Application.Approvals.IDueApprovals>().DueAsync(DateTimeOffset.UtcNow, 500, new HashSet<Guid>(), Ct);
            due.Select(d => d.InstanceId).ShouldNotContain(notDue);
        }

        await SweepUntilAsync(host, rejecting, cancelling, holding);

        (await StateAsync(rejecting)).ShouldBe("Rejected");
        (await StateAsync(cancelling)).ShouldBe("Cancelled");
        (await StateAsync(holding)).ShouldBe("Held");
        (await StateAsync(notDue)).ShouldBe("InProgress");
        (await CountAsync(notDue, "TimeoutsApplied")).ShouldBe(0);
        (await ActorOfLastActionAsync(rejecting)).ShouldBeNull("timeouts are the system's, recorded with no person");
        (await AuditCountAsync(rejecting, WorkflowAuditActions.For(ApprovalActionKind.TimeoutsApplied), WorkflowWorkerHost.Actor)).ShouldBe(1);

        // Held by its timeout, it is no longer due; another pass leaves it alone.
        var sweeper = host.Services.GetRequiredService<ApprovalTimeoutSweeper>();
        await sweeper.RunOnceAsync(Ct);
        (await CountAsync(holding, "TimeoutsApplied")).ShouldBe(1);
    }

    [Fact]
    public async Task APassTakesAtMostTheBatch()
    {
        var now = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            // Older than anything other tests leave behind, so these three come first.
            ids.Add(await StartAsync(now.AddDays(-30).AddMinutes(i), Level(A, TimeoutAction.Escalate)));
        }

        using var host = Worker(batchSize: 2);
        using var scope = host.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IAM.Modules.Workflow.Application.Approvals.IDueApprovals>();
        (await queue.DueAsync(DateTimeOffset.UtcNow, 2, new HashSet<Guid>(), Ct)).Select(d => d.InstanceId).ShouldBe(ids.Take(2));

        // Set-aside ids are left out by the query itself (DL-075g), so the batch is still full and still earliest first.
        var due = await queue.DueAsync(DateTimeOffset.UtcNow, 2, new HashSet<Guid> { ids[0], ids[1], Guid.CreateVersion7() }, Ct);
        due.Count.ShouldBe(2);
        due[0].InstanceId.ShouldBe(ids[2]);
        due.Select(d => d.InstanceId).ShouldNotContain(ids[0]);
        due.Select(d => d.InstanceId).ShouldNotContain(ids[1]);

        var pass = await host.Services.GetRequiredService<ApprovalTimeoutSweeper>().RunOnceAsync(Ct);
        pass.Due.ShouldBe(2);
    }

    [Fact]
    public async Task TwoSweepsAtOnceApplyEachTimeoutOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await StartAsync(now.AddHours(-10), Level(A, TimeoutAction.Reject)));
        }

        using var first = Worker(batchSize: 500);
        using var second = Worker(batchSize: 500);
        for (var round = 0; round < 10 && (await Task.WhenAll(ids.Select(StateAsync))).Any(s => s != "Rejected"); round++)
        {
            await Task.WhenAll(
                first.Services.GetRequiredService<ApprovalTimeoutSweeper>().RunOnceAsync(Ct),
                second.Services.GetRequiredService<ApprovalTimeoutSweeper>().RunOnceAsync(Ct));
        }

        foreach (var id in ids)
        {
            (await StateAsync(id)).ShouldBe("Rejected");
            (await CountAsync(id, "TimeoutsApplied")).ShouldBe(1);
        }

        // A lost race is a conflict on both engines (DL-075h), never a failure.
        first.Services.GetRequiredService<ApprovalTimeoutSweeper>().SetAside.ShouldNotContain(id => ids.Contains(id));
        second.Services.GetRequiredService<ApprovalTimeoutSweeper>().SetAside.ShouldNotContain(id => ids.Contains(id));
    }

    [Fact]
    public async Task ADamagedInstanceIsSetAsideAfterOneAudit()
    {
        var now = DateTimeOffset.UtcNow;
        var id = await StartAsync(now.AddHours(-10), Level(A, TimeoutAction.Reject));
        var version = Convert.ToInt64(await db.ScalarAsync(db.WorkflowAppConnectionString, $"SELECT {Col("EngineVersion")} FROM {Table("ApprovalInstance")} WHERE {Col("Id")} = '{id}'", Ct), CultureInfo.InvariantCulture);
        await db.ExecuteAsync(
            db.WorkflowAppConnectionString,
            $"INSERT INTO {Table("ApprovalHistory")} ({Col("InstanceId")}, {Col("EventVersion")}, {Col("AtUtc")}, {Col("Kind")}, {Col("StageId")}, {Col("Actor")}) VALUES ('{id}', {version + 1}, '2026-11-14T09:00:00+00:00', 'InstanceApproved', NULL, NULL)",
            Ct);

        using var host = Worker(batchSize: 500);
        var sweeper = host.Services.GetRequiredService<ApprovalTimeoutSweeper>();
        for (var round = 0; round < 10 && !sweeper.SetAside.Contains(id); round++)
        {
            await sweeper.RunOnceAsync(Ct);
        }

        sweeper.SetAside.ShouldContain(id);
        await sweeper.RunOnceAsync(Ct);
        (await AuditCountAsync(id, WorkflowAuditActions.Damaged, WorkflowWorkerHost.Actor)).ShouldBe(1);
        (await StateAsync(id)).ShouldBe("InProgress", "nothing about a damaged instance changes");
    }

    private IHost Worker(int batchSize) => WorkflowWorkerHost.Build([],
    [
        new("IAM_DB_PROVIDER", db.Provider.ToString()),
        new("ConnectionStrings:workflow", db.WorkflowAppConnectionString),
        new("ConnectionStrings:catalog", db.CatalogAppConnectionString),
        new("ConnectionStrings:organization", db.OrganizationAppConnectionString),
        new("Workflow:Timeouts:BatchSize", batchSize.ToString(CultureInfo.InvariantCulture)),
        new("Messaging:RabbitMq:Host", "localhost"),
        new("Messaging:RabbitMq:VirtualHost", "iam"),
        new("Messaging:RabbitMq:UserName", "iam-workflow"),
        new("Messaging:RabbitMq:Password", "not-used-here"),
        new("Messaging:RabbitMq:UseTls", "false"),
    ]);

    /// <summary>Other tests leave due instances too, so this runs passes until the ones named here are no longer due.</summary>
    private async Task SweepUntilAsync(IHost host, params Guid[] ids)
    {
        var sweeper = host.Services.GetRequiredService<ApprovalTimeoutSweeper>();
        for (var round = 0; round < 20 && (await Task.WhenAll(ids.Select(id => CountAsync(id, "TimeoutsApplied")))).Any(c => c == 0); round++)
        {
            await sweeper.RunOnceAsync(Ct);
        }
    }

    private static StageDefinition Level(PersonId person, TimeoutAction onTimeout) =>
        new(Guid.CreateVersion7(), 1, QuorumMode.AnyOne, null, false, false, true, TimeSpan.FromHours(4), onTimeout, ApproverSource.Named(person));

    private async Task<Guid> StartAsync(DateTimeOffset at, StageDefinition stage)
    {
        var id = Guid.CreateVersion7();
        var clock = new FixedClock(at);
        var binding = new ApprovalBinding(id, Guid.CreateVersion7(), Guid.CreateVersion7(), new string('c', 64), Guid.CreateVersion7(), Guid.CreateVersion7(), Requester, PersonId.Create("wft-beneficiary"), PersonId.Create("wft-policy-maker"));
        var context = TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString, new AuditingSaveChangesInterceptor(clock, new FixedActor("system:request")));
        var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), clock, new FixedActor("system:request"), new AuditActionCatalog(WorkflowAuditActions.All.Select(x => new AuditActionRegistration(x))));
        var resolver = new ApproverResolver(
            new ApplicationApproverDirectory(TestSupport.CreateCatalog(db.Provider, db.CatalogAppConnectionString)),
            new WorkgroupDirectory(TestSupport.CreateOrganization(db.Provider, db.OrganizationAppConnectionString)),
            new ApproverResolutionSettings());
        var service = new ApprovalWorkflowService(context, writer, clock, TestSupport.ProviderFor(db.Provider), new FixedActor(null), new NoPermissions(), resolver);
        await service.StartAsync(binding, ApprovalRoute.Create([stage]), Ct);
        return id;
    }

    private string Table(string name) => IsSqlServer ? $"[workflow].[{name}]" : $"workflow.{IamModelConventions.ToSnakeCase(name)}";

    private string Col(string name) => IsSqlServer ? $"[{name}]" : IamModelConventions.ToSnakeCase(name);

    private async Task<string> StateAsync(Guid id) =>
        Convert.ToString(await db.ScalarAsync(db.WorkflowAppConnectionString, $"SELECT {Col("State")} FROM {Table("ApprovalInstance")} WHERE {Col("Id")} = '{id}'", Ct), CultureInfo.InvariantCulture)!;

    private async Task<long> CountAsync(Guid id, string kind) =>
        Convert.ToInt64(await db.ScalarAsync(db.WorkflowAppConnectionString, $"SELECT COUNT(*) FROM {Table("ApprovalAction")} WHERE {Col("InstanceId")} = '{id}' AND {Col("Kind")} = '{kind}'", Ct), CultureInfo.InvariantCulture);

    private async Task<string?> ActorOfLastActionAsync(Guid id)
    {
        var sql = IsSqlServer
            ? $"SELECT TOP 1 [Actor] FROM [workflow].[ApprovalAction] WHERE [InstanceId] = '{id}' AND [Kind] = 'TimeoutsApplied' ORDER BY [Sequence] DESC"
            : $"SELECT actor FROM workflow.approval_action WHERE instance_id = '{id}' AND kind = 'TimeoutsApplied' ORDER BY sequence DESC LIMIT 1";
        return await db.ScalarAsync(db.WorkflowAppConnectionString, sql, Ct) is string actor ? actor : null;
    }

    private async Task<long> AuditCountAsync(Guid targetId, string action, string actor)
    {
        var sql = IsSqlServer
            ? $"SELECT COUNT(*) FROM [audit].[AuditEvent] WHERE [TargetId] = '{targetId}' AND [Action] = '{action}' AND [ActorId] = '{actor}'"
            : $"SELECT COUNT(*) FROM audit.audit_event WHERE target_id = '{targetId}' AND action = '{action}' AND actor_id = '{actor}'";
        return Convert.ToInt64(await db.ScalarAsync(db.AuditAppConnectionString, sql, Ct), CultureInfo.InvariantCulture);
    }

    private sealed class NoPermissions : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}

/// <summary>
/// The sweeper's handling of failures (DL-075g), with fakes for the queue and the workflow. No database needed: an instance
/// that keeps failing is set aside after three passes so it can't keep the others waiting; a conflict is not a failure;
/// a cancellation that isn't the worker stopping is counted, not thrown.
/// </summary>
public sealed class ApprovalTimeoutSweeperTests
{
    private static readonly Guid Broken = Guid.CreateVersion7();
    private static readonly Guid Raced = Guid.CreateVersion7();
    private static readonly Guid Slow = Guid.CreateVersion7();
    private static readonly Guid Good = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnInstanceThatKeepsFailingIsSetAsideAfterThreePasses()
    {
        var workflow = new FakeWorkflow();
        var sweeper = Sweeper(workflow, batchSize: 3);

        for (var pass = 1; pass < ApprovalTimeoutSweeper.MaxFailures; pass++)
        {
            (await sweeper.RunOnceAsync(Ct)).ShouldBe(new ApprovalTimeoutPass(3, 0, 1, 2));
            sweeper.SetAside.ShouldBeEmpty();
        }

        (await sweeper.RunOnceAsync(Ct)).ShouldBe(new ApprovalTimeoutPass(3, 0, 1, 2));
        sweeper.SetAside.ShouldBe([Broken, Slow], ignoreOrder: true);

        // With them out of the way, the next one in line gets its timeouts.
        (await sweeper.RunOnceAsync(Ct)).ShouldBe(new ApprovalTimeoutPass(2, 1, 1, 0));
        workflow.Applied.ShouldBe([Good]);
        sweeper.SetAside.ShouldNotContain(Raced);
    }

    [Fact]
    public async Task ASuccessResetsTheCount()
    {
        var workflow = new FakeWorkflow { BrokenSucceedsOnCall = ApprovalTimeoutSweeper.MaxFailures };
        var sweeper = Sweeper(workflow, batchSize: 1);
        // Fails twice, succeeds, fails twice: never three in a row.
        for (var pass = 0; pass < (2 * ApprovalTimeoutSweeper.MaxFailures) - 1; pass++)
        {
            await sweeper.RunOnceAsync(Ct);
        }

        sweeper.SetAside.ShouldNotContain(Broken);
    }

    [Fact]
    public async Task StoppingTheWorkerStillStopsThePass()
    {
        using var stopping = new CancellationTokenSource();
        var workflow = new FakeWorkflow { OnCall = stopping.Cancel };
        var sweeper = Sweeper(workflow, batchSize: 3);
        await Should.ThrowAsync<OperationCanceledException>(() => sweeper.RunOnceAsync(stopping.Token));
    }

    private static ApprovalTimeoutSweeper Sweeper(FakeWorkflow workflow, int batchSize)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAM.Modules.Workflow.Application.Approvals.IDueApprovals>(_ => new FakeQueue());
        services.AddScoped<IAM.Modules.Workflow.Application.Approvals.IApprovalWorkflow>(_ => workflow);
        var provider = services.BuildServiceProvider();
        return new ApprovalTimeoutSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ApprovalTimeoutSettings(batchSize: batchSize),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ApprovalTimeoutSweeper>.Instance);
    }

    /// <summary>Earliest first: the broken one, the raced one, the slow one, then the good one; honours the skip set.</summary>
    private sealed class FakeQueue : IAM.Modules.Workflow.Application.Approvals.IDueApprovals
    {
        public Task<IReadOnlyList<IAM.Modules.Workflow.Application.Approvals.DueApproval>> DueAsync(DateTimeOffset now, int take, IReadOnlySet<Guid> skip, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IAM.Modules.Workflow.Application.Approvals.DueApproval>>(
                [.. new[] { Broken, Raced, Slow, Good }.Where(id => !skip.Contains(id)).Take(take).Select(id => new IAM.Modules.Workflow.Application.Approvals.DueApproval(id, 1))]);
    }

    private sealed class FakeWorkflow : IAM.Modules.Workflow.Application.Approvals.IApprovalWorkflow
    {
        private int _brokenCalls;

        public List<Guid> Applied { get; } = [];

        public int? BrokenSucceedsOnCall { get; init; }

        public Action? OnCall { get; init; }

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView> ApplyTimeoutsAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken)
        {
            OnCall?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (instanceId == Broken && ++_brokenCalls != BrokenSucceedsOnCall)
            {
                throw new InvalidOperationException("broken");
            }

            if (instanceId == Raced)
            {
                throw new IAM.Modules.Workflow.Application.Approvals.ApprovalConflictException("raced");
            }

            if (instanceId == Slow)
            {
                // A provider timeout surfaces as a cancellation that isn't the worker stopping.
                throw new TaskCanceledException("command timeout");
            }

            Applied.Add(instanceId);
            return Task.FromResult<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView>(null!);
        }

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView> StartAsync(ApprovalBinding binding, ApprovalRoute route, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView?> ReadAsync(Guid instanceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalTaskPage> MyTasksAsync(int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView> DecideAsync(Guid instanceId, long expectedVersion, Guid decisionId, Guid stageId, bool approve, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView> CompleteAsync(Guid instanceId, long expectedVersion, Guid completionId, Guid stageId, string? note, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView> ReturnForChangesAsync(Guid instanceId, long expectedVersion, Guid stageId, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView> WithdrawAsync(Guid instanceId, long expectedVersion, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView> CancelAsync(Guid instanceId, long expectedVersion, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView> EscalateAsync(Guid instanceId, long expectedVersion, Guid stageId, IReadOnlyList<PersonId> newCandidates, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView> ProposeEscalationAsync(Guid instanceId, long expectedVersion, Guid stageId, IReadOnlyList<PersonId> newCandidates, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView> ApproveEscalationAsync(Guid instanceId, long expectedVersion, Guid stageId, IReadOnlyList<PersonId> reviewedCandidates, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAM.Modules.Workflow.Application.Approvals.ApprovalInstanceView> WithdrawEscalationAsync(Guid instanceId, long expectedVersion, Guid stageId, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

/// <summary>The worker refuses to start without its settings (DL-074). No database needed.</summary>
public sealed class WorkflowWorkerHostTests
{
    private static readonly KeyValuePair<string, string?>[] Complete =
    [
        new("IAM_DB_PROVIDER", "PostgreSql"),
        new("ConnectionStrings:workflow", "Host=localhost;Database=iam;Username=u;Password=p"),
        new("ConnectionStrings:catalog", "Host=localhost;Database=iam;Username=u;Password=p"),
        new("ConnectionStrings:organization", "Host=localhost;Database=iam;Username=u;Password=p"),
        new("Messaging:RabbitMq:Host", "localhost"),
        new("Messaging:RabbitMq:VirtualHost", "iam"),
        new("Messaging:RabbitMq:UserName", "iam-workflow"),
        new("Messaging:RabbitMq:Password", "p"),
        new("Messaging:RabbitMq:UseTls", "false"),
    ];

    [Fact]
    public void ItStartsOnlyWithEverySetting()
    {
        using (var host = WorkflowWorkerHost.Build([], Complete))
        {
            host.Services.GetRequiredService<ApprovalTimeoutSettings>().Interval.ShouldBe(TimeSpan.FromSeconds(60));
        }

        Should.Throw<Exception>(() => WorkflowWorkerHost.Build([], Complete.Where(s => s.Key != "IAM_DB_PROVIDER")));
        foreach (var schema in new[] { "workflow", "catalog", "organization" })
        {
            Should.Throw<InvalidOperationException>(() => WorkflowWorkerHost.Build([], Complete.Where(s => s.Key != $"ConnectionStrings:{schema}")), schema)
                .Message.ShouldContain(schema);
        }

        Should.Throw<ArgumentOutOfRangeException>(() => WorkflowWorkerHost.Build([], [.. Complete, new("Workflow:Timeouts:IntervalSeconds", "1")]));
        Should.Throw<ArgumentOutOfRangeException>(() => WorkflowWorkerHost.Build([], [.. Complete, new("Workflow:Timeouts:BatchSize", "501")]));

        // The broker (DL-077): every setting needed, never guest, TLS unless on this machine; relay settings bounded.
        foreach (var key in new[] { "Host", "VirtualHost", "UserName", "Password" })
        {
            Should.Throw<InvalidOperationException>(() => WorkflowWorkerHost.Build([], Complete.Where(s => s.Key != $"Messaging:RabbitMq:{key}")), key)
                .Message.ShouldContain(key);
        }

        Should.Throw<InvalidOperationException>(() => WorkflowWorkerHost.Build([], [.. Complete.Where(s => s.Key != "Messaging:RabbitMq:UserName"), new("Messaging:RabbitMq:UserName", "guest")]));
        Should.Throw<InvalidOperationException>(() => WorkflowWorkerHost.Build([], [.. Complete.Where(s => s.Key != "Messaging:RabbitMq:Host"), new("Messaging:RabbitMq:Host", "broker.internal")]));
        Should.Throw<ArgumentOutOfRangeException>(() => WorkflowWorkerHost.Build([], [.. Complete, new("Workflow:Outbox:MaxAttempts", "0")]));
        using (var host = WorkflowWorkerHost.Build([], Complete))
        {
            host.Services.GetRequiredService<OutboxRelaySettings>().BatchSize.ShouldBe(100);
            host.Services.GetServices<IHostedService>().Count().ShouldBe(2, "the timeout worker and the outbox relay");
        }
    }
}
