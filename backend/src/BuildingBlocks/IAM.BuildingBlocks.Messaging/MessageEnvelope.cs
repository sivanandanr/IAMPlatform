using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IAM.BuildingBlocks.Abstractions.Messaging;

namespace IAM.BuildingBlocks.Messaging;

/// <summary>
/// One message as stored in an outbox and sent to the broker (ADR-0006, DL-076). <see cref="IdempotencyKey"/> is fixed by
/// what caused the message, so the same cause never gives two different messages; consumers deduplicate on
/// <see cref="MessageId"/> in their inbox.
/// </summary>
public sealed record MessageEnvelope(
    Guid MessageId,
    string Type,
    int SchemaVersion,
    string Source,
    string CorrelationId,
    string? CausationId,
    string IdempotencyKey,
    DateTimeOffset OccurredAtUtc,
    string Payload)
{
    /// <summary>Type, version and id only: logging an envelope must not expose its payload.</summary>
    public override string ToString() => $"{nameof(MessageEnvelope)} {Type} v{SchemaVersion} {MessageId}";
}

/// <summary>A message that breaks the envelope rules, or isn't the type and version the reader expects. Never retried.</summary>
public sealed class InvalidMessageException : Exception
{
    public InvalidMessageException()
    {
    }

    public InvalidMessageException(string message)
        : base(message)
    {
    }

    public InvalidMessageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Builds, checks and reads <see cref="MessageEnvelope"/>s with one strict JSON setting for every module.</summary>
public static partial class MessageEnvelopes
{
    /// <summary>The largest payload, in characters. A message is a pointer with ids and codes, never a document.</summary>
    public const int MaxPayloadLength = 65_536;

    public const int MaxTypeLength = 100;
    public const int MaxSourceLength = 50;
    public const int MaxCorrelationLength = 100;
    public const int MaxIdempotencyKeyLength = 200;
    public const int MaxSchemaVersion = 1_000;

    /// <summary>Strict JSON: camelCase, enums as names, unknown members refused, bounded depth.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    /// <summary>Wraps <paramref name="message"/> and checks the result; the id is a UUIDv7 at the time it happened.</summary>
    public static MessageEnvelope Create<T>(T message, string source, string correlationId, string? causationId, string idempotencyKey, DateTimeOffset occurredAtUtc)
        where T : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(message);
        var envelope = new MessageEnvelope(
            Guid.CreateVersion7(occurredAtUtc),
            T.MessageType,
            T.SchemaVersion,
            source,
            correlationId,
            causationId,
            idempotencyKey,
            occurredAtUtc,
            JsonSerializer.Serialize(message, Json));
        Validate(envelope);
        return envelope;
    }

    /// <summary>Reads the payload as <typeparamref name="T"/>, refusing any other type or version and anything malformed.</summary>
    public static T Read<T>(MessageEnvelope envelope)
        where T : IIntegrationEvent
    {
        Validate(envelope);
        if (!string.Equals(envelope.Type, T.MessageType, StringComparison.Ordinal) || envelope.SchemaVersion != T.SchemaVersion)
        {
            throw new InvalidMessageException("The message is not the expected type and version.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(envelope.Payload, Json) ?? throw new InvalidMessageException("The message payload is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidMessageException("The message payload is malformed.", ex);
        }
    }

    /// <summary>Checks every field's shape and bound. Throws <see cref="InvalidMessageException"/>, naming the field only.</summary>
    public static void Validate(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Require(envelope.MessageId != Guid.Empty, nameof(envelope.MessageId));
        Require(Matches(envelope.Type, MaxTypeLength, TypePattern()), nameof(envelope.Type));
        Require(envelope.SchemaVersion is >= 1 and <= MaxSchemaVersion, nameof(envelope.SchemaVersion));
        Require(Matches(envelope.Source, MaxSourceLength, SourcePattern()), nameof(envelope.Source));
        Require(Matches(envelope.CorrelationId, MaxCorrelationLength, KeyPattern()), nameof(envelope.CorrelationId));
        Require(envelope.CausationId is null || Matches(envelope.CausationId, MaxCorrelationLength, KeyPattern()), nameof(envelope.CausationId));
        Require(Matches(envelope.IdempotencyKey, MaxIdempotencyKeyLength, KeyPattern()), nameof(envelope.IdempotencyKey));
        Require(envelope.OccurredAtUtc.Offset == TimeSpan.Zero, nameof(envelope.OccurredAtUtc));
        Require(envelope.Payload is { Length: > 1 and <= MaxPayloadLength } && envelope.Payload[0] == '{', nameof(envelope.Payload));
    }

    private static bool Matches(string? value, int maxLength, Regex pattern) =>
        value is not null && value.Length <= maxLength && pattern.IsMatch(value);

    private static void Require(bool ok, string field)
    {
        if (!ok)
        {
            throw new InvalidMessageException($"The message's {field} is missing or not allowed.");
        }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){1,5}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TypePattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SourcePattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]*\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex KeyPattern();
}

/// <summary>
/// The handler can't decide yet (what it reads is still being written, DL-100): the message is tried again later without
/// counting towards the delivery limit, a bounded number of times, then counted like any failure.
/// </summary>
public sealed class MessageDeferredException : Exception
{
    public MessageDeferredException()
    {
    }

    public MessageDeferredException(string message)
        : base(message)
    {
    }

    public MessageDeferredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
