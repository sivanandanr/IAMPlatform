using IAM.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;

namespace IAM.BuildingBlocks.Persistence;

/// <summary>
/// One message a consumer has processed (ADR-0006, DL-021, DL-078), in the consuming module's own schema. The key is the
/// consumer and the message id, so the same message can't be processed twice by one consumer. Append-only: the module's
/// migration refuses changes and deletes.
/// </summary>
public sealed class InboxMessageRow
{
    public required string ConsumerName { get; init; }

    public required Guid MessageId { get; init; }

    public required string Type { get; init; }

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
    }

    /// <summary>
    /// Processes <paramref name="envelope"/> at most once for <paramref name="consumer"/> (DL-078): in one transaction,
    /// checks the inbox, records the message and runs <paramref name="effects"/>, which only add or change tracked
    /// entities. A message already there, or recorded by a concurrent delivery first, is <see cref="MessageOutcome.Duplicate"/>
    /// and changes nothing.
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
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await inbox.AnyAsync(i => i.ConsumerName == consumer && i.MessageId == envelope.MessageId, cancellationToken).ConfigureAwait(false))
        {
            return MessageOutcome.Duplicate;
        }

        inbox.Add(new InboxMessageRow { ConsumerName = consumer, MessageId = envelope.MessageId, Type = envelope.Type, ReceivedAtUtc = now });
        effects();
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (provider.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return MessageOutcome.Duplicate;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MessageOutcome.Processed;
    }
}
