using System.Globalization;
using RabbitMQ.Client;

namespace IAM.BuildingBlocks.Messaging.RabbitMq;

/// <summary>
/// Broker names (DL-077, DL-079). The topology itself comes from the reviewed <c>infra/rabbitmq/definitions.json</c>,
/// applied by an operator: no module may create, change or delete an exchange or queue, so the code only names them. Each
/// module publishes to its own topic exchange <c>iam.{source}</c>, routed by message type. A message no queue is bound for
/// goes to <c>iam.unrouted</c>, a short, capped safety net (at most 7 days, oldest dropped first): it is never delivered
/// to a consumer created later.
/// </summary>
public static class RabbitMqTopology
{
    public const string UnroutedExchange = "iam.unrouted";
    public const string UnroutedQueue = "iam.unrouted";

    /// <summary>How many times a consumer has tried a message: 1 for the first delivery (DL-078).</summary>
    public const string AttemptHeader = "iam-attempt";

    public static string ExchangeFor(string source) => "iam." + source;

    /// <summary>A consumer's queue: <c>iam.</c> and its name.</summary>
    public static string QueueFor(IMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return "iam." + handler.ConsumerName;
    }

    /// <summary>The consumer's own dead-letter exchange (DL-079): no consumer can reach another's dead letters.</summary>
    public static string DeadLetterExchangeFor(IMessageHandler handler) => QueueFor(handler) + ".dlx";

    public static string DeadLetterQueueFor(IMessageHandler handler) => QueueFor(handler) + ".dlq";

    /// <summary>Where a consumer republishes a failed message; its queue returns it to the main queue after the retry delay.</summary>
    public static string RetryExchangeFor(IMessageHandler handler) => QueueFor(handler) + ".retry";

    /// <summary>Where the retry queue returns a message to the consumer's queue, routed by the queue's name.</summary>
    public static string ReturnExchangeFor(IMessageHandler handler) => QueueFor(handler) + ".return";

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
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) && attempt >= 1 ? attempt : 1;
    }

    /// <summary>
    /// A broker failure as a short code, never message text (DL-079): the broker's refusals (403 access refused, 404 no
    /// such exchange or queue, 406 arguments differ from the definitions) are configuration errors, told apart from a lost
    /// connection so operators can see which they have.
    /// </summary>
    public static string ErrorCode(Exception exception) => exception switch
    {
        RabbitMQ.Client.Exceptions.PublishException { IsReturn: true } => "PublishReturned",
        RabbitMQ.Client.Exceptions.PublishException => "PublishNacked",
        RabbitMQ.Client.Exceptions.OperationInterruptedException { ShutdownReason.ReplyCode: 403 } => "BrokerAccessRefused",
        RabbitMQ.Client.Exceptions.OperationInterruptedException { ShutdownReason.ReplyCode: 404 } => "BrokerNotFound",
        RabbitMQ.Client.Exceptions.OperationInterruptedException { ShutdownReason.ReplyCode: 406 } => "BrokerPreconditionFailed",
        OperationCanceledException => "PublishTimeout",
        _ => exception.GetType().Name,
    };

    /// <summary>True for a refusal that retrying won't fix: the topology or permissions are wrong.</summary>
    public static bool IsConfigurationError(string code) =>
        code is "BrokerAccessRefused" or "BrokerNotFound" or "BrokerPreconditionFailed" or "PublishReturned";
}

/// <summary>Sends one message and returns only once the broker has confirmed it; anything else throws.</summary>
public interface IMessagePublisher
{
    Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes to RabbitMQ with publisher confirms (DL-077): persistent messages, mandatory routing, the envelope's ids in
/// the message properties and headers, the payload as the body. It declares nothing: the exchange comes from the broker
/// definitions (DL-079). One connection and channel, opened on first use and thrown away after any failure so the next
/// send starts clean. Not for concurrent use: one relay owns it.
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
