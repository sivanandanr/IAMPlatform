using IAM.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;

namespace IAM.BuildingBlocks.Persistence;

/// <summary>
/// One message in a module's outbox (ADR-0006, DL-021, DL-076): written in the same transaction as the change it reports,
/// in the module's own schema, by the module's own login. What the message says never changes; only its delivery fields
/// do, and the module's migration enforces both with triggers. Nothing deletes a row yet (retention comes with the relay).
/// </summary>
public sealed class OutboxMessageRow
{
    public required Guid MessageId { get; init; }

    public required string Type { get; init; }

    public required int SchemaVersion { get; init; }

    public required string Source { get; init; }

    public required string CorrelationId { get; init; }

    public string? CausationId { get; init; }

    /// <summary>Unique: the same cause can't be written twice.</summary>
    public required string IdempotencyKey { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }

    public required string Payload { get; init; }

    /// <summary>Delivery: how many sends were tried. Never goes down.</summary>
    public int Attempts { get; set; }

    /// <summary>Delivery: not before this time, after a failed send.</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; set; }

    /// <summary>Delivery: when the broker confirmed it. Once set, never changes.</summary>
    public DateTimeOffset? DispatchedAtUtc { get; set; }

    /// <summary>Delivery: the last failure's code, never its message text.</summary>
    public string? LastError { get; set; }

    public static OutboxMessageRow From(MessageEnvelope envelope)
    {
        MessageEnvelopes.Validate(envelope);
        return new OutboxMessageRow
        {
            MessageId = envelope.MessageId,
            Type = envelope.Type,
            SchemaVersion = envelope.SchemaVersion,
            Source = envelope.Source,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            IdempotencyKey = envelope.IdempotencyKey,
            OccurredAtUtc = envelope.OccurredAtUtc,
            Payload = envelope.Payload,
        };
    }

    public MessageEnvelope ToEnvelope() =>
        new(MessageId, Type, SchemaVersion, Source, CorrelationId, CausationId, IdempotencyKey, OccurredAtUtc, Payload);
}

public static class OutboxModel
{
    public const string TableName = "OutboxMessage";
    public const int MaxErrorLength = 100;

    /// <summary>Maps <see cref="OutboxMessageRow"/> to the module's <c>OutboxMessage</c> table, guarded by <paramref name="triggerName"/>.</summary>
    public static void ConfigureOutbox(this ModelBuilder modelBuilder, string triggerName)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var outbox = modelBuilder.Entity<OutboxMessageRow>();
        outbox.ToTable(TableName, t => t.HasTrigger(triggerName));
        outbox.HasKey(x => x.MessageId);
        outbox.Property(x => x.Type).IsRequired().IsCode(MessageEnvelopes.MaxTypeLength);
        outbox.Property(x => x.Source).IsRequired().IsCode(MessageEnvelopes.MaxSourceLength);
        outbox.Property(x => x.CorrelationId).IsRequired().IsCode(MessageEnvelopes.MaxCorrelationLength);
        outbox.Property(x => x.CausationId).IsCode(MessageEnvelopes.MaxCorrelationLength);
        outbox.Property(x => x.IdempotencyKey).IsRequired().IsCode(MessageEnvelopes.MaxIdempotencyKeyLength);
        outbox.HasIndex(x => x.IdempotencyKey).IsUnique();
        outbox.Property(x => x.Payload).IsRequired().HasMaxLength(MessageEnvelopes.MaxPayloadLength);
        outbox.Property(x => x.LastError).IsCode(MaxErrorLength);
        outbox.HasIndex(x => new { x.DispatchedAtUtc, x.OccurredAtUtc });
    }
}
