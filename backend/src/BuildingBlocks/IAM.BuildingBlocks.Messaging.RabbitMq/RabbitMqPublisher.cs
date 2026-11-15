using System.Globalization;
using RabbitMQ.Client;

namespace IAM.BuildingBlocks.Messaging.RabbitMq;

/// <summary>
/// Broker names (DL-077). Each module publishes to its own durable topic exchange <c>iam.{source}</c>, routed by message
/// type. A message no queue is bound for goes to the alternate exchange <c>iam.unrouted</c> and its quorum queue, so
/// nothing is dropped before its consumers exist.
/// </summary>
public static class RabbitMqTopology
{
    public const string UnroutedExchange = "iam.unrouted";
    public const string UnroutedQueue = "iam.unrouted";

    public static string ExchangeFor(string source) => "iam." + source;

    /// <summary>The exchange every consumer's dead letters go to, routed by the consumer's queue name.</summary>
    public const string DeadLetterExchange = "iam.dlx";

    /// <summary>A consumer's queue: <c>iam.</c> and its name.</summary>
    public static string QueueFor(IMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return "iam." + handler.ConsumerName;
    }

    public static string DeadLetterQueueFor(IMessageHandler handler) => QueueFor(handler) + ".dlq";

    /// <summary>Where a consumer republishes a failed message; its queue returns it to the main queue after the retry delay.</summary>
    public static string RetryExchangeFor(IMessageHandler handler) => QueueFor(handler) + ".retry";

    /// <summary>Where the retry queue returns a message to the consumer's queue, routed by the queue's name.</summary>
    public static string ReturnExchangeFor(IMessageHandler handler) => QueueFor(handler) + ".return";

    /// <summary>How many times a consumer has tried a message: 1 for the first delivery (DL-078).</summary>
    public const string AttemptHeader = "iam-attempt";

    public static int AttemptOf(IReadOnlyBasicProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var text = properties.Headers?.TryGetValue(AttemptHeader, out var value) == true
            ? value switch
            {
                byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                string s => s,
                _ => null,
            }
            : null;
        return int.TryParse(text, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) && attempt >= 1 ? attempt : 1;
    }

    /// <summary>
    /// Declares a consumer's quorum queue, bound to its source module's exchange for exactly the types it takes, with its
    /// own dead-letter queue and retry queue, and the broker's delivery limit as a back-stop (DL-078). The source exchange must already exist: only its module may
    /// declare it. Safe to repeat.
    /// </summary>
    public static async Task<string> DeclareConsumerAsync(IChannel channel, IMessageHandler handler, int deliveryLimit, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var queue = QueueFor(handler);
        var dlq = DeadLetterQueueFor(handler);
        var retry = RetryExchangeFor(handler);

        // Retry: a direct exchange and a queue that holds a failed message for the retry delay, then returns it to the
        // main queue through the consumer's own return exchange (the default exchange would need broader permissions).
        var back = ReturnExchangeFor(handler);
        await channel.ExchangeDeclareAsync(back, ExchangeType.Direct, durable: true, autoDelete: false, arguments: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.ExchangeDeclareAsync(retry, ExchangeType.Direct, durable: true, autoDelete: false, arguments: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueDeclareAsync(
            retry,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-message-ttl"] = (long)retryDelay.TotalMilliseconds,
                ["x-dead-letter-exchange"] = back,
                ["x-dead-letter-routing-key"] = queue,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueBindAsync(retry, retry, queue, cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, arguments: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueDeclareAsync(
            dlq,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueBindAsync(dlq, DeadLetterExchange, queue, cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueDeclareAsync(
            queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = deliveryLimit,
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = queue,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueBindAsync(queue, back, queue, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var type in handler.MessageTypes)
        {
            await channel.QueueBindAsync(queue, ExchangeFor(handler.Source), type, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return queue;
    }

    /// <summary>Declares the module's exchange and the unrouted catch-all. Safe to repeat.</summary>
    public static async Task DeclareAsync(IChannel channel, string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        await channel.ExchangeDeclareAsync(UnroutedExchange, ExchangeType.Fanout, durable: true, autoDelete: false, arguments: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueDeclareAsync(
            UnroutedQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueBindAsync(UnroutedQueue, UnroutedExchange, string.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.ExchangeDeclareAsync(
            ExchangeFor(source),
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["alternate-exchange"] = UnroutedExchange },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Sends one message and returns only once the broker has confirmed it; anything else throws.</summary>
public interface IMessagePublisher
{
    Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes to RabbitMQ with publisher confirms (DL-077): persistent messages, mandatory routing, the envelope's ids in
/// the message properties and headers, the payload as the body. One connection and channel, opened on first use and
/// thrown away after any failure so the next send starts clean. Not for concurrent use: one relay owns it.
/// </summary>
public sealed class RabbitMqPublisher(RabbitMqSettings settings, string source) : IMessagePublisher, IAsyncDisposable
{
    public const string SchemaVersionHeader = "iam-schema-version";
    public const string SourceHeader = "iam-source";
    public const string CausationHeader = "iam-causation-id";
    public const string IdempotencyHeader = "iam-idempotency-key";
    public const string OccurredAtHeader = "iam-occurred-at";

    private IConnection? _connection;
    private IChannel? _channel;

    /// <summary>The message properties for an envelope: what <see cref="RabbitMqDelivery.ToEnvelope"/> reads back.</summary>
    public static BasicProperties PropertiesFor(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new BasicProperties
        {
            MessageId = envelope.MessageId.ToString("D"),
            Type = envelope.Type,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = DeliveryModes.Persistent,
            CorrelationId = envelope.CorrelationId,
            AppId = envelope.Source,
            Timestamp = new AmqpTimestamp(envelope.OccurredAtUtc.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>
            {
                [SchemaVersionHeader] = envelope.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                [SourceHeader] = envelope.Source,
                [CausationHeader] = envelope.CausationId,
                [IdempotencyHeader] = envelope.IdempotencyKey,
                [OccurredAtHeader] = envelope.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
            },
        };
    }

    public async Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        MessageEnvelopes.Validate(envelope);
        if (!string.Equals(envelope.Source, source, StringComparison.Ordinal))
        {
            throw new InvalidMessageException("A relay publishes only its own module's messages.");
        }

        try
        {
            var channel = await ChannelAsync(cancellationToken).ConfigureAwait(false);
            var properties = PropertiesFor(envelope);

            // With confirmation tracking on, this completes when the broker confirms, and throws on a nack or a return.
            await channel.BasicPublishAsync(
                RabbitMqTopology.ExchangeFor(source),
                envelope.Type,
                mandatory: true,
                properties,
                System.Text.Encoding.UTF8.GetBytes(envelope.Payload),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ResetAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync() => await ResetAsync().ConfigureAwait(false);

    private async Task<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await ResetAsync().ConfigureAwait(false);
        var factory = new ConnectionFactory
        {
            HostName = settings.Host,
            Port = settings.Port,
            VirtualHost = settings.VirtualHost,
            UserName = settings.UserName,
            Password = settings.Password,
            RequestedConnectionTimeout = settings.ConnectTimeout,
            AutomaticRecoveryEnabled = false,
            ClientProvidedName = "iam-relay-" + source,
            Ssl = new SslOption { Enabled = settings.UseTls, ServerName = settings.Host },
        };
        _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken).ConfigureAwait(false);
        await RabbitMqTopology.DeclareAsync(_channel, source, cancellationToken).ConfigureAwait(false);
        return _channel;
    }

    private async Task ResetAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;
        if (channel is not null)
        {
            await SwallowAsync(channel.DisposeAsync).ConfigureAwait(false);
        }

        if (connection is not null)
        {
            await SwallowAsync(connection.DisposeAsync).ConfigureAwait(false);
        }
    }

    private static async Task SwallowAsync(Func<ValueTask> dispose)
    {
        try
        {
            await dispose().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Closing a connection that already failed must never hide the failure that caused it.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
