using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace IAM.BuildingBlocks.Messaging.RabbitMq;

/// <summary>A consumer's settings (<c>{module}:Consumer</c>), checked when the worker is built (DL-078, DL-079).</summary>
public sealed class ConsumerSettings
{
    /// <summary>The broker's own delivery limit on every consumer queue (definitions.json): a back-stop above any consumer's.</summary>
    public const int BrokerDeliveryLimit = 20;

    public ConsumerSettings(int? prefetch = null, int? deliveryLimit = null, int? reconnectDelaySeconds = null)
    {
        var count = prefetch ?? 10;
        if (count is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(prefetch), "Prefetch must be 1 to 100.");
        }

        Prefetch = (ushort)count;
        DeliveryLimit = deliveryLimit ?? 5;
        if (DeliveryLimit < 1 || DeliveryLimit > BrokerDeliveryLimit - 5)
        {
            throw new ArgumentOutOfRangeException(nameof(deliveryLimit), "DeliveryLimit must be 1 to 15, below the broker's back-stop.");
        }

        ReconnectDelay = TimeSpan.FromSeconds(reconnectDelaySeconds ?? 5);
        if (ReconnectDelay < TimeSpan.FromSeconds(1) || ReconnectDelay > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(reconnectDelaySeconds), "ReconnectDelaySeconds must be 1 to 60.");
        }
    }

    /// <summary>Unacknowledged messages one consumer holds at a time.</summary>
    public ushort Prefetch { get; }

    /// <summary>Attempts after which a failing message is dead-lettered (counted by the consumer, DL-078).</summary>
    public int DeliveryLimit { get; }

    /// <summary>The first wait before reconnecting; it doubles on each failure, up to a minute. The retry delay of a failed message is in the broker definitions.</summary>
    public TimeSpan ReconnectDelay { get; }

    public static ConsumerSettings From(IConfiguration configuration, string section)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var s = configuration.GetSection(section);
        return new ConsumerSettings(s.GetValue<int?>("Prefetch"), s.GetValue<int?>("DeliveryLimit"), s.GetValue<int?>("ReconnectDelaySeconds"));
    }
}

/// <summary>What to tell the broker about one delivery.</summary>
public enum DeliveryDisposition
{
    /// <summary>Processed, or already processed: done.</summary>
    Ack = 1,

    /// <summary>Can never be processed: to the dead-letter queue now.</summary>
    Reject = 2,

    /// <summary>Failed for now: tried again after the retry delay, until the delivery limit dead-letters it.</summary>
    Retry = 3,
}

/// <summary>
/// Reads a broker delivery back into a <see cref="MessageEnvelope"/>, refusing anything the consumer didn't ask for or
/// that didn't come from where it says (DL-078): the exchange must be the source module's, the routing key the type, the
/// type one the consumer takes, the source header the consumer's source, and every envelope rule must hold. A message
/// returning from the consumer's own retry queue comes through its return exchange, routed by its queue name.
/// </summary>
public static class RabbitMqDelivery
{
    public static MessageEnvelope ToEnvelope(string exchange, string routingKey, IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body, IMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(handler);
        // Two routes only: from the source module's exchange by type, or back from this consumer's own retry queue.
        var fromSource = string.Equals(exchange, RabbitMqTopology.ExchangeFor(handler.Source), StringComparison.Ordinal)
            && string.Equals(routingKey, properties.Type, StringComparison.Ordinal);
        var retried = string.Equals(exchange, RabbitMqTopology.ReturnExchangeFor(handler), StringComparison.Ordinal)
            && string.Equals(routingKey, RabbitMqTopology.QueueFor(handler), StringComparison.Ordinal);
        Require(fromSource || retried, "exchange");
        Require(properties.Type is { } type && handler.MessageTypes.Contains(type, StringComparer.Ordinal), "type");
        Require(string.Equals(Header(properties, RabbitMqPublisher.SourceHeader), handler.Source, StringComparison.Ordinal), "source");
        Require(Guid.TryParseExact(properties.MessageId, "D", out var messageId), "message id");
        Require(int.TryParse(Header(properties, RabbitMqPublisher.SchemaVersionHeader), NumberStyles.None, CultureInfo.InvariantCulture, out var version), "schema version");
        Require(
            DateTimeOffset.TryParseExact(Header(properties, RabbitMqPublisher.OccurredAtHeader), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var occurredAt),
            "time");
        Require(body.Length is > 1 and <= MessageEnvelopes.MaxPayloadLength * 3, "payload size");

        string payload;
        try
        {
            payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body.Span);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidMessageException("The message payload is not valid UTF-8.", ex);
        }

        var envelope = new MessageEnvelope(
            messageId,
            properties.Type!,
            version,
            handler.Source,
            properties.CorrelationId ?? string.Empty,
            Header(properties, RabbitMqPublisher.CausationHeader),
            Header(properties, RabbitMqPublisher.IdempotencyHeader) ?? string.Empty,
            occurredAt,
            payload);
        MessageEnvelopes.Validate(envelope);
        return envelope;
    }

    private static string? Header(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers?.TryGetValue(name, out var value) == true
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null,
            }
            : null;

    private static void Require(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidMessageException($"The delivery's {what} is missing or not what this consumer takes.");
        }
    }
}

/// <summary>
/// Consumes one handler's queue (ADR-0006, DL-078, DL-079). Each delivery is read back into an envelope and handed to a
/// fresh <typeparamref name="THandler"/> in its own scope; it is acknowledged only after the handler's transaction
/// committed, rejected to the consumer's dead-letter queue if it can never be processed, and on any other failure
/// republished to its retry queue (the original acknowledged once the copy is confirmed) until the delivery limit
/// dead-letters it. Anything that goes wrong while handling a delivery ends the run: the connection closes, the broker
/// delivers unacknowledged messages again, and the consumer reconnects with a back-off, so it never sits idle with
/// messages stuck. It declares nothing: its queues come from the broker definitions. Logs carry a message id and type
/// only once the delivery has been validated.
/// </summary>
public sealed partial class RabbitMqConsumer<THandler>(
    IServiceScopeFactory scopes,
    RabbitMqSettings broker,
    ConsumerSettings settings,
    ILogger<RabbitMqConsumer<THandler>> logger) : BackgroundService
    where THandler : class, IMessageHandler
{
    /// <summary>How long shutdown waits for deliveries being handled to finish.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private int _inFlight;

    /// <summary>Handles one delivery and says what to tell the broker. Public for tests; the worker calls it per delivery.</summary>
    public async Task<DeliveryDisposition> DispatchAsync(string exchange, string routingKey, IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(properties);
        try
        {
            using var scope = scopes.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<THandler>();
            MessageEnvelope envelope;
            try
            {
                envelope = RabbitMqDelivery.ToEnvelope(exchange, routingKey, properties, body, handler);
            }
            catch (InvalidMessageException)
            {
                // The delivery's own ids are not trusted yet, so they are not logged.
                LogRejectedUnread(logger, handler.ConsumerName, exchange);
                return DeliveryDisposition.Reject;
            }

            // The handling runs under the message's correlation id, so what it audits and sends carries it on (DL-094).
            using var trace = MessageCorrelation.StartHandling(envelope, handler.ConsumerName);
            try
            {
                var outcome = await handler.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
                if (outcome == MessageOutcome.Duplicate)
                {
                    LogDuplicate(logger, handler.ConsumerName, envelope.MessageId, envelope.Type);
                }

                return DeliveryDisposition.Ack;
            }
            catch (InvalidMessageException)
            {
                LogRejected(logger, handler.ConsumerName, envelope.MessageId, envelope.Type);
                return DeliveryDisposition.Reject;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogRetry(logger, handler.ConsumerName, envelope.MessageId, envelope.Type, ex.GetType().Name);
                return DeliveryDisposition.Retry;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Reading the delivery or building the handler failed (a regular-expression timeout, a broken registration).
            LogRetryUnread(logger, typeof(THandler).Name, ex.GetType().Name);
            return DeliveryDisposition.Retry;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var subscribed = false;
            try
            {
                subscribed = await RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                var code = RabbitMqTopology.ErrorCode(ex);
                if (RabbitMqTopology.IsConfigurationError(code))
                {
                    LogConfigurationError(logger, typeof(THandler).Name, code);
                }
                else
                {
                    LogConnectionLost(logger, typeof(THandler).Name, code);
                }
            }

            failures = subscribed ? 0 : failures + 1;
            if (!stoppingToken.IsCancellationRequested)
            {
                var wait = TimeSpan.FromTicks(Math.Min(settings.ReconnectDelay.Ticks << Math.Min(failures, 6), TimeSpan.FromMinutes(1).Ticks));
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs until the connection, either channel or the subscription ends, or a delivery fails to be settled. True once it was subscribed.</summary>
    private async Task<bool> RunAsync(CancellationToken stoppingToken)
    {
        IMessageHandler subscription;
        using (var scope = scopes.CreateScope())
        {
            subscription = scope.ServiceProvider.GetRequiredService<THandler>();
        }

        var queue = RabbitMqTopology.QueueFor(subscription);
        var factory = new ConnectionFactory
        {
            HostName = broker.Host,
            Port = broker.Port,
            VirtualHost = broker.VirtualHost,
            UserName = broker.UserName,
            Password = broker.Password,
            RequestedConnectionTimeout = broker.ConnectTimeout,
            AutomaticRecoveryEnabled = false,
            ClientProvidedName = "iam-consumer-" + subscription.ConsumerName,
            Ssl = new SslOption { Enabled = broker.UseTls, ServerName = broker.Host },
        };
        await using var connection = await factory.CreateConnectionAsync(stoppingToken).ConfigureAwait(false);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);

        // Retries are republished, and the original acknowledged only once the broker confirmed the copy.
        await using var retries = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            stoppingToken).ConfigureAwait(false);
        await channel.BasicQosAsync(0, settings.Prefetch, global: false, stoppingToken).ConfigureAwait(false);

        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task End(object? sender, ShutdownEventArgs args)
        {
            ended.TrySetResult();
            return Task.CompletedTask;
        }

        connection.ConnectionShutdownAsync += End;
        channel.ChannelShutdownAsync += End;
        retries.ChannelShutdownAsync += End;

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ShutdownAsync += End;

        // The broker cancels the subscription when the queue is deleted: that ends the run too.
        consumer.UnregisteredAsync += (_, _) =>
        {
            ended.TrySetResult();
            return Task.CompletedTask;
        };
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                await SettleAsync(channel, retries, subscription, queue, delivery, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Couldn't settle it (the retry copy wasn't confirmed, a channel closed): end the run so the broker
                // delivers it again rather than leaving it unacknowledged and the consumer stalled.
                LogSettleFailed(logger, subscription.ConsumerName, RabbitMqTopology.ErrorCode(ex));
                ended.TrySetResult();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken).ConfigureAwait(false);
        if (!connection.IsOpen || !channel.IsOpen || !retries.IsOpen)
        {
            return false;
        }

        LogConsuming(logger, subscription.ConsumerName, queue);
        await Task.WhenAny(ended.Task, Task.Delay(Timeout.Infinite, stoppingToken)).ConfigureAwait(false);
        if (!stoppingToken.IsCancellationRequested)
        {
            LogConnectionLost(logger, subscription.ConsumerName, "Closed");
        }

        // Let deliveries being handled finish before the channel and connection go.
        var until = DateTime.UtcNow + DrainTimeout;
        while (Volatile.Read(ref _inFlight) > 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
        }

        return true;
    }

    private async Task SettleAsync(IChannel channel, IChannel retries, IMessageHandler subscription, string queue, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        var disposition = await DispatchAsync(delivery.Exchange, delivery.RoutingKey, delivery.BasicProperties, delivery.Body, stoppingToken).ConfigureAwait(false);
        switch (disposition)
        {
            case DeliveryDisposition.Ack:
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken).ConfigureAwait(false);
                break;
            case DeliveryDisposition.Reject:
                await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, stoppingToken).ConfigureAwait(false);
                break;
            default:
                // Counted here, not by the broker: a requeued message isn't counted towards a quorum queue's delivery
                // limit (RabbitMQ 4), so requeueing would retry a failing message without end (DL-078).
                var attempt = RabbitMqTopology.AttemptOf(delivery.BasicProperties);
                if (attempt >= settings.DeliveryLimit)
                {
                    LogGivenUp(logger, subscription.ConsumerName, attempt);
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, stoppingToken).ConfigureAwait(false);
                    break;
                }

                var copy = new BasicProperties(delivery.BasicProperties)
                {
                    Headers = new Dictionary<string, object?>(delivery.BasicProperties.Headers ?? new Dictionary<string, object?>())
                    {
                        [RabbitMqTopology.AttemptHeader] = (attempt + 1).ToString(CultureInfo.InvariantCulture),
                    },
                };
                await retries.BasicPublishAsync(RabbitMqTopology.RetryExchangeFor(subscription), queue, mandatory: true, copy, delivery.Body, stoppingToken).ConfigureAwait(false);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken).ConfigureAwait(false);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Consumer {Consumer} is reading {Queue}.")]
    private static partial void LogConsuming(ILogger logger, string consumer, string queue);

    [LoggerMessage(Level = LogLevel.Information, Message = "Consumer {Consumer} had already processed message {MessageId} ({Type}); acknowledged with no effect.")]
    private static partial void LogDuplicate(ILogger logger, string consumer, Guid messageId, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} received a delivery from {Exchange} that failed validation; sent to its dead-letter queue.")]
    private static partial void LogRejectedUnread(ILogger logger, string consumer, string exchange);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} can't process message {MessageId} ({Type}); sent to its dead-letter queue.")]
    private static partial void LogRejected(ILogger logger, string consumer, Guid messageId, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} failed on message {MessageId} ({Type}) with {ErrorType}; tried again later.")]
    private static partial void LogRetry(ILogger logger, string consumer, Guid messageId, string type, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} failed to read a delivery with {ErrorType}; tried again later.")]
    private static partial void LogRetryUnread(ILogger logger, string consumer, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Consumer {Consumer} gave up on a message after {Attempts} attempts; sent to its dead-letter queue.")]
    private static partial void LogGivenUp(ILogger logger, string consumer, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Consumer {Consumer} couldn't settle a delivery ({ErrorCode}); reconnecting so it is delivered again.")]
    private static partial void LogSettleFailed(ILogger logger, string consumer, string errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Consumer {Consumer} is refused by the broker ({ErrorCode}): its permissions or the broker definitions are wrong; retrying.")]
    private static partial void LogConfigurationError(ILogger logger, string consumer, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} lost its broker connection ({ErrorCode}); reconnecting.")]
    private static partial void LogConnectionLost(ILogger logger, string consumer, string errorCode);
}
