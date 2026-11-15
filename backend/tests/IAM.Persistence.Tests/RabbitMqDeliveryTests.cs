using System.Text;
using IAM.BuildingBlocks.Messaging;
using IAM.BuildingBlocks.Messaging.RabbitMq;
using IAM.Modules.Workflow.Contracts;
using RabbitMQ.Client;
using Shouldly;

namespace IAM.Persistence.Tests;

/// <summary>A consumer reads a delivery back into the envelope that was sent, and refuses anything else (DL-078). No broker.</summary>
public sealed class RabbitMqDeliveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 30, 0, 123, TimeSpan.Zero);

    private sealed class TaskHandler : IMessageHandler
    {
        public string ConsumerName => "notification.approval_tasks";

        public string Source => "workflow";

        public IReadOnlyList<string> MessageTypes => [ApprovalTaskOpened.MessageType];

        public Task<MessageOutcome> HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken) => Task.FromResult(MessageOutcome.Processed);
    }

    private static readonly TaskHandler Handler = new();

    private static MessageEnvelope Opened() => MessageEnvelopes.Create(
        new ApprovalTaskOpened(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "Decision", ["a"], T0.AddHours(4), 3),
        "workflow",
        "corr",
        "workflow.action:x:1",
        "workflow.action:x:1:task",
        T0);

    private static MessageEnvelope Read(MessageEnvelope envelope, Action<BasicProperties>? change = null, string? exchange = null, string? routingKey = null, byte[]? body = null)
    {
        var properties = RabbitMqPublisher.PropertiesFor(envelope);
        change?.Invoke(properties);
        return RabbitMqDelivery.ToEnvelope(
            exchange ?? "iam.workflow",
            routingKey ?? envelope.Type,
            properties,
            body ?? Encoding.UTF8.GetBytes(envelope.Payload),
            Handler);
    }

    [Fact]
    public void WhatThePublisherSendsReadsBackExactly()
    {
        var envelope = Opened();
        Read(envelope).ShouldBe(envelope);

        // Back from the consumer's own retry queue.
        Read(envelope, exchange: "iam.notification.approval_tasks.return", routingKey: "iam.notification.approval_tasks").ShouldBe(envelope);
    }

    [Fact]
    public void ADeliveryThatIsNotWhatTheConsumerTakesIsRefused()
    {
        var envelope = Opened();
        Action[] refused =
        [
            () => Read(envelope, exchange: "iam.notification"),
            () => Read(envelope, exchange: "iam.notification.approval_tasks.return"),
            () => Read(envelope, exchange: "iam.other.return", routingKey: "iam.notification.approval_tasks"),
            () => Read(envelope, routingKey: ApprovalClosed.MessageType),
            () => Read(envelope, p => p.Type = ApprovalClosed.MessageType, routingKey: ApprovalClosed.MessageType),
            () => Read(envelope, p => p.Headers![RabbitMqPublisher.SourceHeader] = "notification"),
            () => Read(envelope, p => p.Headers!.Remove(RabbitMqPublisher.SourceHeader)),
            () => Read(envelope, p => p.MessageId = "not-a-guid"),
            () => Read(envelope, p => p.Headers![RabbitMqPublisher.SchemaVersionHeader] = "one"),
            () => Read(envelope, p => p.Headers![RabbitMqPublisher.OccurredAtHeader] = "yesterday"),
            () => Read(envelope, p => p.Headers![RabbitMqPublisher.OccurredAtHeader] = "2026-11-15T10:30:00.0000000+01:00"),
            () => Read(envelope, p => p.Headers![RabbitMqPublisher.IdempotencyHeader] = null),
            () => Read(envelope, p => p.CorrelationId = null),
            () => Read(envelope, body: [0xC3, 0x28, 0x7B, 0x7D]),
            () => Read(envelope, body: new byte[(MessageEnvelopes.MaxPayloadLength * 3) + 1]),
        ];
        foreach (var read in refused)
        {
            Should.Throw<InvalidMessageException>(read);
        }
    }

    [Fact]
    public void ConsumerSettingsAreBounded()
    {
        var defaults = new ConsumerSettings();
        defaults.Prefetch.ShouldBe((ushort)10);
        defaults.DeliveryLimit.ShouldBe(5);
        Should.Throw<ArgumentOutOfRangeException>(() => new ConsumerSettings(prefetch: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new ConsumerSettings(prefetch: 101));
        Should.Throw<ArgumentOutOfRangeException>(() => new ConsumerSettings(deliveryLimit: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new ConsumerSettings(deliveryLimit: ConsumerSettings.BrokerDeliveryLimit - 4));
        new ConsumerSettings(deliveryLimit: ConsumerSettings.BrokerDeliveryLimit - 5).DeliveryLimit.ShouldBe(15);
        Should.Throw<ArgumentOutOfRangeException>(() => new ConsumerSettings(reconnectDelaySeconds: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new ConsumerSettings(reconnectDelaySeconds: 61));
    }
}
