using IAM.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Notification.Infrastructure.Persistence;

/// <summary>
/// Notification module DbContext, schema <c>notification</c> (ADR-0006, ADR-0017, DL-078): the inbox of messages it has
/// processed, and the notifications waiting to be sent. Nothing sends them yet.
/// </summary>
public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "notification";

    public override string Schema => SchemaName;

    public DbSet<InboxMessageRow> Inbox => Set<InboxMessageRow>();

    public DbSet<PendingNotificationRow> Pending => Set<PendingNotificationRow>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigureInbox("TR_InboxMessage_AppendOnly");

        var pending = modelBuilder.Entity<PendingNotificationRow>();
        pending.ToTable("PendingNotification");
        pending.HasKey(x => x.Id);
        pending.Property(x => x.Kind).IsRequired().IsCode(50);
        pending.Property(x => x.Recipient).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        pending.HasIndex(x => new { x.SourceMessageId, x.Recipient }).IsUnique();
        pending.HasIndex(x => new { x.Recipient, x.CreatedForUtc });
    }
}

/// <summary>
/// Someone should be told something (DL-078). Only ids and codes: the message is composed when it is sent, from data the
/// recipient may see then. One per source message and recipient.
/// </summary>
public sealed class PendingNotificationRow
{
    public required Guid Id { get; init; }

    /// <summary>What it is about, for example <c>approval.task</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The person's sign-in id.</summary>
    public required string Recipient { get; init; }

    public required Guid InstanceId { get; init; }

    public required Guid RequestItemId { get; init; }

    public Guid? StageId { get; init; }

    public DateTimeOffset? DeadlineUtc { get; init; }

    /// <summary>The event it came from, so the same event can't notify the same person twice.</summary>
    public required Guid SourceMessageId { get; init; }

    /// <summary>When the event happened.</summary>
    public required DateTimeOffset CreatedForUtc { get; init; }
}
