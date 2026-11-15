using IAM.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;

namespace IAM.BuildingBlocks.Persistence;

/// <summary>
/// One message a consumer has processed (ADR-0006, DL-021, DL-078, DL-079), in the consuming module's own schema. The key is
/// the consumer and the message id, and the consumer, source and idempotency key are unique too, so neither the same
/// message nor the same cause sent again under a new id is processed twice. Append-only: the module's migration refuses
/// changes and deletes.
/// </summary>
public sealed class InboxMessageRow
{
    public required string ConsumerName { get; init; }

    public required Guid MessageId { get; init; }

    public required string Type { get; init; }

    public required string Source { get; init; }

    public required string IdempotencyKey { get; init; }

    public required DateTimeOffset ReceivedAtUtc { get; init; }
}

public static class InboxModel
{
    public const string TableName = "InboxMessage";
    public const int MaxConsumerNameLength = 100;

    /// <summary>Maps <see cref="InboxMessageRow"/> to the module's <c>InboxMessage</c> table, guarded by <paramref name="triggerName"/>.</summary>
    public static void ConfigureInbox(this ModelBuilder modelBuilder, string triggerName)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var inbox = modelBuilder.Entity<InboxMessageRow>();
        inbox.ToTable(TableName, t => t.HasTrigger(triggerName));
        inbox.IsAppendOnly();
        inbox.HasKey(x => new { x.ConsumerName, x.MessageId });
        inbox.Property(x => x.ConsumerName).IsRequired().IsCode(MaxConsumerNameLength);
        inbox.Property(x => x.Type).IsRequired().IsCode(MessageEnvelopes.MaxTypeLength);
        inbox.Property(x => x.Source).IsRequired().IsCode(MessageEnvelopes.MaxSourceLength);
        inbox.Property(x => x.IdempotencyKey).IsRequired().IsCode(MessageEnvelopes.MaxIdempotencyKeyLength);
        inbox.HasIndex(x => new { x.ConsumerName, x.Source, x.IdempotencyKey }).IsUnique();
    }

    /// <summary>
    /// Processes <paramref name="envelope"/> at most once for <paramref name="consumer"/> (DL-078): in one transaction,
    /// checks the inbox, records the message and runs <paramref name="effects"/>, which only add or change tracked
    /// entities. A message already there (by id, or by its source's idempotency key), or recorded by a concurrent delivery
    /// first, is <see cref="MessageOutcome.Duplicate"/> and changes nothing. Any other unique-key clash is the effects' own
    /// and is thrown, so the message is retried rather than acknowledged with its effects lost (DL-079).
    /// </summary>
    public static async Task<MessageOutcome> ReceiveOnceAsync(
        this DbContext db,
        IIamDatabaseProvider provider,
        string consumer,
        MessageEnvelope envelope,
        DateTimeOffset now,
        Action effects,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(effects);
        var inbox = db.Set<InboxMessageRow>();
        var id = envelope.MessageId;
        var source = envelope.Source;
        var key = envelope.IdempotencyKey;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await inbox.AnyAsync(i => i.ConsumerName == consumer && (i.MessageId == id || (i.Source == source && i.IdempotencyKey == key)), cancellationToken).ConfigureAwait(false))
        {
            return MessageOutcome.Duplicate;
        }

        inbox.Add(new InboxMessageRow
        {
            ConsumerName = consumer,
            MessageId = id,
            Type = envelope.Type,
            Source = source,
            IdempotencyKey = key,
            ReceivedAtUtc = now,
        });
        effects();
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (provider.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            // A concurrent delivery of the same message committed first: its inbox row is there now.
            if (await inbox.AsNoTracking().AnyAsync(i => i.ConsumerName == consumer && (i.MessageId == id || (i.Source == source && i.IdempotencyKey == key)), cancellationToken).ConfigureAwait(false))
            {
                return MessageOutcome.Duplicate;
            }

            throw;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MessageOutcome.Processed;
    }
}
