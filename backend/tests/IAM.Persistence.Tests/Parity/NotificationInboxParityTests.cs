using System.Collections.Concurrent;
using System.Text;
using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Messaging.RabbitMq;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Catalog.Infrastructure.Applications;
using IAM.Modules.Notification.Infrastructure.Approvals;
using IAM.Modules.Notification.Infrastructure.Persistence;
using IAM.Modules.Organization.Infrastructure.Workgroups;
using IAM.Modules.Workflow.Application.Approvers;
using IAM.Modules.Workflow.Contracts;
using IAM.Modules.Workflow.Domain.Approvals;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using IAM.Modules.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// The Notification module's inbox on both engines with a real broker and per-module broker users (DL-078, P0-010): a
/// duplicate message does not duplicate an operation; bad messages go to the dead-letter queue unprocessed; a failure is
/// retried and processed once, or dead-lettered at the delivery limit; one module can't publish as another or read its
/// queues; and a workflow action reaches the people it concerns end to end.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class NotificationInboxParityTests(ParityDatabase db, RabbitMqBroker broker) : IClassFixture<RabbitMqBroker>
{
    private const int DeliveryLimit = 2;
    private static readonly PersonId A = PersonId.Create("nt-a");
    private static readonly PersonId B = PersonId.Create("nt-b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    [Fact]
    public void TheMigrationAppliesOnce()
    {
        db.NotificationFirstRunApplied.Count.ShouldBe(1);
        db.NotificationFirstRunApplied[0].ShouldEndWith("_NotificationInbox");
        db.NotificationSecondRunPending.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADuplicateMessageDoesNotDuplicateTheOperation()
    {
        var envelope = Opened(A, B);
        var consumer = Consumer<ApprovalTaskNotifications>(Services());
        var properties = RabbitMqPublisher.PropertiesFor(envelope);
        var body = Encoding.UTF8.GetBytes(envelope.Payload);

        (await consumer.DispatchAsync("iam.workflow", envelope.Type, properties, body, Ct)).ShouldBe(DeliveryDisposition.Ack);
        (await consumer.DispatchAsync("iam.workflow", envelope.Type, properties, body, Ct)).ShouldBe(DeliveryDisposition.Ack);
        (await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => consumer.DispatchAsync("iam.workflow", envelope.Type, properties, body, Ct))))
            .ShouldAllBe(d => d == DeliveryDisposition.Ack, "concurrent duplicates are acknowledged, not retried");

        (await PendingAsync(envelope.MessageId)).Select(p => p.Recipient).ShouldBe([A.Value, B.Value], ignoreOrder: true);
        (await InboxCountAsync(envelope.MessageId)).ShouldBe(1);

        await using var scope = Services().CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<ApprovalTaskNotifications>().HandleAsync(envelope, Ct)).ShouldBe(MessageOutcome.Duplicate);

        // First arrivals at the same moment: each consumer sees no inbox row, one insert wins, the others are duplicates.
        for (var round = 0; round < 5; round++)
        {
            var fresh = Opened(A, B);
            var freshProperties = RabbitMqPublisher.PropertiesFor(fresh);
            var freshBody = Encoding.UTF8.GetBytes(fresh.Payload);
            var consumers = Enumerable.Range(0, 6).Select(_ => Consumer<ApprovalTaskNotifications>(Services())).ToList();
            (await Task.WhenAll(consumers.Select(c => c.DispatchAsync("iam.workflow", fresh.Type, freshProperties, freshBody, Ct))))
                .ShouldAllBe(d => d == DeliveryDisposition.Ack, "a concurrent duplicate is acknowledged, never retried");
            (await PendingAsync(fresh.MessageId)).Count.ShouldBe(2);
            (await InboxCountAsync(fresh.MessageId)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task AnEventNamingBadPeopleIsRejectedAndChangesNothing()
    {
        var consumer = Consumer<ApprovalTaskNotifications>(Services());
        foreach (var people in new[] { new[] { "nt-a", "nt-a" }, [" nt-a"], [] })
        {
            var envelope = Opened(people);
            (await consumer.DispatchAsync("iam.workflow", envelope.Type, RabbitMqPublisher.PropertiesFor(envelope), Encoding.UTF8.GetBytes(envelope.Payload), Ct))
                .ShouldBe(DeliveryDisposition.Reject);
            (await InboxCountAsync(envelope.MessageId)).ShouldBe(0);
        }
    }

    [Fact]
    public async Task AMessageFromElsewhereIsRejectedAtOnceNotRetried()
    {
        var consumer = Consumer<ApprovalTaskNotifications>(Services());
        var envelope = Opened(A);
        var body = Encoding.UTF8.GetBytes(envelope.Payload);
        var foreign = RabbitMqPublisher.PropertiesFor(envelope);
        foreign.Headers![RabbitMqPublisher.SourceHeader] = "notification";
        (await consumer.DispatchAsync("iam.workflow", envelope.Type, foreign, body, Ct)).ShouldBe(DeliveryDisposition.Reject);
        (await consumer.DispatchAsync("iam.notification", envelope.Type, RabbitMqPublisher.PropertiesFor(envelope), body, Ct)).ShouldBe(DeliveryDisposition.Reject);
        (await InboxCountAsync(envelope.MessageId)).ShouldBe(0);
    }

    [Fact]
    public async Task BadMessagesGoToTheDeadLetterQueueUnprocessed()
    {
        await DeclareTopologyAsync();
        var malformed = Opened(A) with { Payload = """{"instanceId":1}""" };
        var newVersion = Opened(A);
        var foreign = Opened(A);

        await using (var connection = await broker.ConnectAsync(Ct))
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: Ct))
        {
            await RawPublishAsync(channel, malformed, _ => { });
            await RawPublishAsync(channel, newVersion, p => p.Headers![RabbitMqPublisher.SchemaVersionHeader] = "2");
            await RawPublishAsync(channel, foreign, p => p.Headers![RabbitMqPublisher.SourceHeader] = "notification");
        }

        await using (await RunningAsync(Consumer<ApprovalTaskNotifications>(Services())))
        {
            var dead = await DeadLettersAsync([malformed.MessageId, newVersion.MessageId, foreign.MessageId]);
            dead.ShouldBe([malformed.MessageId, newVersion.MessageId, foreign.MessageId], ignoreOrder: true);
        }

        foreach (var id in new[] { malformed.MessageId, newVersion.MessageId, foreign.MessageId })
        {
            (await InboxCountAsync(id)).ShouldBe(0);
            (await PendingAsync(id)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task AFailureIsRetriedAndProcessedOnceOrDeadLetteredAtTheLimit()
    {
        await DeclareTopologyAsync();
        var flaky = Opened(A);
        var broken = Opened(B);
        FlakyHandler.FailuresLeft[flaky.MessageId] = 1;
        FlakyHandler.FailuresLeft[broken.MessageId] = int.MaxValue;

        await using (var publisher = new RabbitMqPublisher(broker.Workflow, WorkflowEvents.Source))
        {
            await publisher.PublishAsync(flaky, Ct);
            await publisher.PublishAsync(broken, Ct);
        }

        await using (await RunningAsync(Consumer<FlakyHandler>(Services())))
        {
            await EventuallyAsync(async () => (await PendingAsync(flaky.MessageId)).Count == 1);
            (await DeadLettersAsync([broken.MessageId])).ShouldBe([broken.MessageId], $"attempts {FlakyHandler.Attempts.GetValueOrDefault(broken.MessageId)}");
        }

        (await InboxCountAsync(flaky.MessageId)).ShouldBe(1);
        FlakyHandler.Attempts[flaky.MessageId].ShouldBe(2);
        FlakyHandler.Attempts[broken.MessageId].ShouldBe(DeliveryLimit);
        (await InboxCountAsync(broken.MessageId)).ShouldBe(0);
    }

    [Fact]
    public async Task AModuleCanNeitherPublishAsAnotherNorReadItsQueues()
    {
        await DeclareTopologyAsync();
        var envelope = Opened(A);

        // The notification user can't write to the workflow exchange.
        await using (var forger = new RabbitMqPublisher(broker.Notification, WorkflowEvents.Source))
        {
            await Should.ThrowAsync<Exception>(() => forger.PublishAsync(envelope, Ct));
        }

        // The workflow user can't read or create the notification module's queues.
        await using (var connection = await RabbitMqBroker.ConnectAsync(broker.Workflow, Ct))
        {
            await using (var channel = await connection.CreateChannelAsync(cancellationToken: Ct))
            {
                await Should.ThrowAsync<Exception>(() => channel.BasicGetAsync("iam.notification.approval_tasks", autoAck: true, Ct));
            }

            await using (var channel = await connection.CreateChannelAsync(cancellationToken: Ct))
            {
                await Should.ThrowAsync<Exception>(() => channel.QueueDeclareAsync("iam.notification.forged", durable: true, exclusive: false, autoDelete: false, cancellationToken: Ct));
            }
        }

        (await InboxCountAsync(envelope.MessageId)).ShouldBe(0);
    }

    [Fact]
    public async Task AWorkflowActionReachesThePeopleItConcernsEndToEnd()
    {
        await DeclareTopologyAsync();
        var id = Guid.CreateVersion7();
        var started = await Workflow().StartAsync(Binding(id), ApprovalRoute.Create([Level(1, A, B)]), Ct);

        await using (await RunningAsync(Consumer<ApprovalTaskNotifications>(Services())))
        await using (var publisher = new RabbitMqPublisher(broker.Workflow, WorkflowEvents.Source))
        {
            await Relay(publisher).RunOnceAsync(Ct);
            await EventuallyAsync(async () => (await PendingForInstanceAsync(id)).Count == 2);
        }

        var pending = await PendingForInstanceAsync(id);
        pending.Select(p => p.Recipient).ShouldBe([A.Value, B.Value], ignoreOrder: true);
        pending.ShouldAllBe(p => p.Kind == ApprovalTaskNotifications.Kind && p.StageId == started.Stages[0].StageId && p.RequestItemId == started.RequestItemId);
    }

    [Fact]
    public async Task TheInboxCannotBeChangedOrEmptied()
    {
        var envelope = Opened(A);
        var consumer = Consumer<ApprovalTaskNotifications>(Services());
        await consumer.DispatchAsync("iam.workflow", envelope.Type, RabbitMqPublisher.PropertiesFor(envelope), Encoding.UTF8.GetBytes(envelope.Payload), Ct);
        var table = IsSqlServer ? "[notification].[InboxMessage]" : "notification.inbox_message";
        var where = IsSqlServer ? $"WHERE [MessageId] = '{envelope.MessageId}'" : $"WHERE message_id = '{envelope.MessageId}'";
        var type = IsSqlServer ? "[Type]" : "type";

        await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.NotificationAppConnectionString, $"DELETE FROM {table} {where}", Ct));
        await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.NotificationAppConnectionString, $"UPDATE {table} SET {type} = 'workflow.approval.closed' {where}", Ct));
        if (!IsSqlServer)
        {
            await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, "TRUNCATE notification.inbox_message", Ct));
        }

        (await InboxCountAsync(envelope.MessageId)).ShouldBe(1);
    }

    private static MessageEnvelope Opened(params PersonId[] people) => Opened([.. people.Select(p => p.Value)]);

    private static MessageEnvelope Opened(string[] people) => MessageEnvelopes.Create(
        new ApprovalTaskOpened(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "Decision", people, DateTimeOffset.UtcNow.AddHours(4), 3),
        WorkflowEvents.Source,
        "notification-test",
        null,
        $"notification-test:{Guid.CreateVersion7():N}",
        DateTimeOffset.UtcNow);

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => TestSupport.CreateNotification(db.Provider, db.NotificationAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor("system:notifications"))));
        services.AddSingleton(TestSupport.ProviderFor(db.Provider));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ApprovalTaskNotifications>();
        services.AddScoped<FlakyHandler>();
        return services.BuildServiceProvider();
    }

    private RabbitMqConsumer<T> Consumer<T>(ServiceProvider services)
        where T : class, IMessageHandler =>
        new(services.GetRequiredService<IServiceScopeFactory>(), broker.Notification, new ConsumerSettings(deliveryLimit: DeliveryLimit, retryDelaySeconds: 1), NullLogger<RabbitMqConsumer<T>>.Instance);

    /// <summary>The workflow module's exchange, then the consumer's queues, as each module's own user would declare them.</summary>
    private async Task DeclareTopologyAsync()
    {
        await using (var connection = await RabbitMqBroker.ConnectAsync(broker.Workflow, Ct))
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: Ct))
        {
            await RabbitMqTopology.DeclareAsync(channel, WorkflowEvents.Source, Ct);
        }

        await using (var connection = await RabbitMqBroker.ConnectAsync(broker.Notification, Ct))
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: Ct))
        {
            await RabbitMqTopology.DeclareConsumerAsync(channel, new FlakyHandler(null!), DeliveryLimit, TimeSpan.FromSeconds(1), Ct);
        }
    }

    private static async Task RawPublishAsync(IChannel channel, MessageEnvelope envelope, Action<BasicProperties> change)
    {
        var properties = RabbitMqPublisher.PropertiesFor(envelope);
        change(properties);
        await channel.BasicPublishAsync("iam.workflow", envelope.Type, mandatory: false, properties, Encoding.UTF8.GetBytes(envelope.Payload), Ct);
    }

    private static async Task<IAsyncDisposable> RunningAsync<T>(RabbitMqConsumer<T> consumer)
        where T : class, IMessageHandler
    {
        await consumer.StartAsync(Ct);
        return new Stopper(consumer);
    }

    /// <summary>Reads the dead-letter queue until all of <paramref name="ids"/> are there or time runs out; returns those found.</summary>
    private async Task<List<Guid>> DeadLettersAsync(IReadOnlyCollection<Guid> ids)
    {
        var wanted = ids.Select(i => i.ToString("D")).ToHashSet();
        var found = new List<Guid>();
        await using var connection = await broker.ConnectAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && found.Count < wanted.Count)
        {
            var message = await channel.BasicGetAsync("iam.notification.approval_tasks.dlq", autoAck: true, Ct);
            if (message is null)
            {
                await Task.Delay(200, Ct);
            }
            else if (message.BasicProperties.MessageId is { } id && wanted.Contains(id))
            {
                found.Add(Guid.Parse(id));
            }
        }

        return found;
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the condition never held");
            await Task.Delay(200, Ct);
        }
    }

    private async Task<List<PendingNotificationRow>> PendingAsync(Guid messageId)
    {
        await using var context = TestSupport.CreateNotification(db.Provider, db.NotificationAppConnectionString);
        return await context.Pending.AsNoTracking().Where(p => p.SourceMessageId == messageId).ToListAsync(Ct);
    }

    private async Task<List<PendingNotificationRow>> PendingForInstanceAsync(Guid instanceId)
    {
        await using var context = TestSupport.CreateNotification(db.Provider, db.NotificationAppConnectionString);
        return await context.Pending.AsNoTracking().Where(p => p.InstanceId == instanceId).ToListAsync(Ct);
    }

    private async Task<int> InboxCountAsync(Guid messageId)
    {
        await using var context = TestSupport.CreateNotification(db.Provider, db.NotificationAppConnectionString);
        return await context.Inbox.AsNoTracking().CountAsync(i => i.MessageId == messageId, Ct);
    }

    private ApprovalWorkflowService Workflow()
    {
        var clock = TimeProvider.System;
        var context = TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString, new AuditingSaveChangesInterceptor(clock, new FixedActor("system:request")));
        var writer = new AuditWriter(
            TestSupport.ProviderFor(db.Provider),
            clock,
            new FixedActor("system:request"),
            new AuditActionCatalog(WorkflowAuditActions.All.Select(a => new AuditActionRegistration(a))));
        var resolver = new ApproverResolver(
            new ApplicationApproverDirectory(TestSupport.CreateCatalog(db.Provider, db.CatalogAppConnectionString)),
            new WorkgroupDirectory(TestSupport.CreateOrganization(db.Provider, db.OrganizationAppConnectionString)),
            new ApproverResolutionSettings());
        return new ApprovalWorkflowService(context, writer, clock, TestSupport.ProviderFor(db.Provider), new FixedActor(null), new NoPermissions(), resolver);
    }

    private OutboxRelay<WorkflowDbContext> Relay(IMessagePublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor("system:relay-test"))));
        return new OutboxRelay<WorkflowDbContext>(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            publisher,
            new OutboxRelaySettings(batchSize: 500),
            TimeProvider.System,
            NullLogger<OutboxRelay<WorkflowDbContext>>.Instance);
    }

    private static ApprovalBinding Binding(Guid id) =>
        new(id, Guid.CreateVersion7(), Guid.CreateVersion7(), new string('d', 64), Guid.CreateVersion7(), Guid.CreateVersion7(),
            PersonId.Create("nt-requester"), PersonId.Create("nt-beneficiary"), PersonId.Create("nt-policy-maker"));

    private static StageDefinition Level(int sequence, params PersonId[] people) =>
        new(Guid.CreateVersion7(), sequence, QuorumMode.AnyOne, null, false, false, true, TimeSpan.FromHours(4), TimeoutAction.Escalate, ApproverSource.Named(people));

    private sealed class Stopper(Microsoft.Extensions.Hosting.BackgroundService consumer) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await consumer.StopAsync(CancellationToken.None);
            consumer.Dispose();
        }
    }

    private sealed class NoPermissions : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    /// <summary>The real handler, failing a set number of times for chosen messages first (a database blip, say).</summary>
    private sealed class FlakyHandler(ApprovalTaskNotifications inner) : IMessageHandler
    {
        public static ConcurrentDictionary<Guid, int> FailuresLeft { get; } = new();

        public static ConcurrentDictionary<Guid, int> Attempts { get; } = new();

        public string ConsumerName => "notification.approval_tasks";

        public string Source => WorkflowEvents.Source;

        public IReadOnlyList<string> MessageTypes { get; } = [ApprovalTaskOpened.MessageType];

        public Task<MessageOutcome> HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
        {
            Attempts.AddOrUpdate(envelope.MessageId, 1, (_, n) => n + 1);
            if (FailuresLeft.TryGetValue(envelope.MessageId, out var left) && left > 0)
            {
                FailuresLeft[envelope.MessageId] = left == int.MaxValue ? left : left - 1;
                throw new InvalidOperationException("Transient failure for the test.");
            }

            return inner.HandleAsync(envelope, cancellationToken);
        }
    }
}
