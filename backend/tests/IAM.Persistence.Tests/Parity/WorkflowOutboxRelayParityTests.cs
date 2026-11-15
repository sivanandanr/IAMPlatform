using System.Text;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Messaging.RabbitMq;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Workflow.Contracts;
using IAM.Modules.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// The workflow outbox relay against a real broker on both engines (DL-077): confirmed messages are marked sent and arrive
/// with their ids; unrouted ones are kept; an unreachable broker leaves messages waiting, retried after a back-off and
/// parked after the last attempt; two relays at once send each message once.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class WorkflowOutboxRelayParityTests(ParityDatabase db, RabbitMqBroker broker) : IClassFixture<RabbitMqBroker>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConfirmedMessagesAreMarkedSentAndArriveWithTheirIds()
    {
        await using var connection = await broker.ConnectAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        var queue = await BoundQueueAsync(channel);
        var ids = await WriteAsync(3);

        await using var publisher = Publisher(broker.Workflow);
        await Relay(publisher).RunOnceAsync(Ct);

        foreach (var row in await RowsAsync(ids))
        {
            row.DispatchedAtUtc.ShouldNotBeNull();
            row.Attempts.ShouldBe(0, "only a failed send counts as an attempt");
            row.LastError.ShouldBeNull();
        }

        var received = await ReceiveAsync(channel, queue, ids);
        received.Count.ShouldBe(3);
        foreach (var (id, message) in received)
        {
            var row = (await RowsAsync([id])).Single();
            message.BasicProperties.MessageId.ShouldBe(id.ToString("D"));
            message.BasicProperties.Type.ShouldBe(ApprovalClosed.MessageType);
            message.BasicProperties.DeliveryMode.ShouldBe(DeliveryModes.Persistent);
            message.BasicProperties.CorrelationId.ShouldBe(row.CorrelationId);
            Header(message, RabbitMqPublisher.SchemaVersionHeader).ShouldBe("1");
            Header(message, RabbitMqPublisher.IdempotencyHeader).ShouldBe(row.IdempotencyKey);
            message.RoutingKey.ShouldBe(ApprovalClosed.MessageType);
            message.Exchange.ShouldBe("iam.workflow");
            Encoding.UTF8.GetString(message.Body.Span).ShouldBe(row.Payload);
        }
    }

    [Fact]
    public async Task AMessageNoQueueWantsIsKeptNotDropped()
    {
        var ids = await WriteAsync(1);
        await using var publisher = Publisher(broker.Workflow);
        await Relay(publisher).RunOnceAsync(Ct);
        (await RowsAsync(ids)).Single().DispatchedAtUtc.ShouldNotBeNull();

        await using var connection = await broker.ConnectAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        (await ReceiveAsync(channel, RabbitMqTopology.UnroutedQueue, ids)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnUnreachableBrokerLeavesMessagesWaitingRetriedThenParked()
    {
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var ids = await WriteAsync(1);
        var settings = new OutboxRelaySettings(batchSize: 500, maxAttempts: 2, publishTimeoutSeconds: 2, leaseSeconds: 30);

        await using (var down = Publisher(broker.Unreachable()))
        {
            await Relay(down, settings, clock).RunOnceAsync(Ct);
            var failed = (await RowsAsync(ids)).Single();
            failed.DispatchedAtUtc.ShouldBeNull();
            failed.Attempts.ShouldBe(1);
            failed.LastError.ShouldNotBeNull().ShouldMatch("^[A-Za-z]+$", "a type name, never message text that could carry hosts or data");
            failed.NextAttemptAtUtc.ShouldNotBeNull().ShouldBeGreaterThan(clock.GetUtcNow());

            // Not due again until the back-off has passed.
            await Relay(down, settings, clock).RunOnceAsync(Ct);
            (await RowsAsync(ids)).Single().Attempts.ShouldBe(1);

            clock.Advance(OutboxRelaySettings.Backoff(1) + TimeSpan.FromSeconds(1));
            await Relay(down, settings, clock).RunOnceAsync(Ct);
            (await RowsAsync(ids)).Single().Attempts.ShouldBe(2);
        }

        // Parked after the last attempt: even a working broker doesn't get it under these settings.
        clock.Advance(TimeSpan.FromHours(1));
        await using var up = Publisher(broker.Workflow);
        await Relay(up, settings, clock).RunOnceAsync(Ct);
        (await RowsAsync(ids)).Single().DispatchedAtUtc.ShouldBeNull();

        // Allowing more attempts (an operator's decision) sends it.
        await Relay(up, new OutboxRelaySettings(batchSize: 500, maxAttempts: 3), clock).RunOnceAsync(Ct);
        var sent = (await RowsAsync(ids)).Single();
        sent.DispatchedAtUtc.ShouldNotBeNull();
        sent.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task TwoRelaysAtOnceSendEachMessageOnce()
    {
        await using var connection = await broker.ConnectAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        var queue = await BoundQueueAsync(channel);
        var ids = await WriteAsync(20);

        await using var first = Publisher(broker.Workflow);
        await using var second = Publisher(broker.Workflow);
        await Task.WhenAll(Relay(first).RunOnceAsync(Ct), Relay(second).RunOnceAsync(Ct));

        (await RowsAsync(ids)).ShouldAllBe(r => r.DispatchedAtUtc != null && r.Attempts == 0);
        var received = await ReceiveAsync(channel, queue, ids, expected: 20);
        received.Count.ShouldBe(20);
        received.Select(r => r.Id).Distinct().Count().ShouldBe(20, "no message was sent twice");
    }

    [Fact]
    public async Task ARelaySendsOnlyItsOwnModulesMessages()
    {
        await using var publisher = Publisher(broker.Workflow);
        var foreign = MessageEnvelopes.Create(new ApprovalClosed(Guid.CreateVersion7(), Guid.CreateVersion7(), "Approved", true, 3), "notification", "c1", null, "k1", DateTimeOffset.UtcNow);
        await Should.ThrowAsync<InvalidMessageException>(() => publisher.PublishAsync(foreign, Ct));
    }

    [Fact]
    public async Task ARowThatBreaksTheRulesIsParkedAtOnce()
    {
        // DL-079: a row the relay can never send (here another module's message in the workflow outbox) isn't retried.
        await using var context = Context();
        var foreign = OutboxMessageRow.From(MessageEnvelopes.Create(
            new ApprovalClosed(Guid.CreateVersion7(), Guid.CreateVersion7(), "Approved", true, 5),
            "notification",
            "relay-test",
            null,
            $"relay-test:{Guid.CreateVersion7():N}",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        context.Outbox.Add(foreign);
        await context.SaveChangesAsync(Ct);

        await using var publisher = Publisher(broker.Workflow);
        await Relay(publisher).RunOnceAsync(Ct);
        var row = (await RowsAsync([foreign.MessageId])).Single();
        row.Attempts.ShouldBe(new OutboxRelaySettings().MaxAttempts);
        row.LastError.ShouldBe("InvalidMessage");
        row.DispatchedAtUtc.ShouldBeNull();
    }

    /// <summary>Writes closed events dated years ago, so they come first in the relay's earliest-first batch.</summary>
    private async Task<List<Guid>> WriteAsync(int count)
    {
        await using var context = Context();
        var at = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 1_000_000));
        var rows = Enumerable.Range(0, count)
            .Select(i => OutboxMessageRow.From(MessageEnvelopes.Create(
                new ApprovalClosed(Guid.CreateVersion7(), Guid.CreateVersion7(), "Approved", true, 5),
                WorkflowEvents.Source,
                "relay-test",
                null,
                $"relay-test:{Guid.CreateVersion7():N}",
                at.AddMilliseconds(i))))
            .ToList();
        context.Outbox.AddRange(rows);
        await context.SaveChangesAsync(Ct);
        return [.. rows.Select(r => r.MessageId)];
    }

    private async Task<List<OutboxMessageRow>> RowsAsync(IReadOnlyCollection<Guid> ids)
    {
        await using var context = Context();
        return await context.Outbox.AsNoTracking().Where(m => ids.Contains(m.MessageId)).ToListAsync(Ct);
    }

    private WorkflowDbContext Context() =>
        TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor("system:relay-test")));

    private static RabbitMqPublisher Publisher(RabbitMqSettings settings) => new(settings, WorkflowEvents.Source);

    private OutboxRelay<WorkflowDbContext> Relay(IMessagePublisher publisher, OutboxRelaySettings? settings = null, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Context());
        return new OutboxRelay<WorkflowDbContext>(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            publisher,
            settings ?? new OutboxRelaySettings(batchSize: 500),
            clock ?? TimeProvider.System,
            NullLogger<OutboxRelay<WorkflowDbContext>>.Instance);
    }

    /// <summary>A queue that receives everything the workflow exchange routes, deleted with its connection.</summary>
    private static async Task<string> BoundQueueAsync(IChannel channel)
    {
        var queue = await channel.QueueDeclareAsync(string.Empty, durable: false, exclusive: true, autoDelete: true, cancellationToken: Ct);
        await channel.QueueBindAsync(queue.QueueName, "iam.workflow", "#", cancellationToken: Ct);
        return queue.QueueName;
    }

    /// <summary>Reads the queue until every wanted message has arrived (others are acknowledged and ignored) or time runs out.</summary>
    private static async Task<List<(Guid Id, BasicGetResult Message)>> ReceiveAsync(IChannel channel, string queue, IReadOnlyCollection<Guid> ids, int? expected = null)
    {
        var wanted = ids.Select(i => i.ToString("D")).ToHashSet();
        var found = new List<(Guid, BasicGetResult)>();
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until && found.Count < (expected ?? wanted.Count) + (expected is null ? 0 : 1))
        {
            var message = await channel.BasicGetAsync(queue, autoAck: true, Ct);
            if (message is null)
            {
                if (found.Count >= (expected ?? wanted.Count))
                {
                    break;
                }

                await Task.Delay(100, Ct);
                continue;
            }

            if (message.BasicProperties.MessageId is { } id && wanted.Contains(id))
            {
                found.Add((Guid.Parse(id), message));
            }
        }

        return found;
    }

    private static string? Header(BasicGetResult message, string name) =>
        message.BasicProperties.Headers?.TryGetValue(name, out var value) == true && value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;

    private sealed class MovingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
