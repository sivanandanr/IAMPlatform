using IAM.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Workflow.Infrastructure.Persistence;

/// <summary>
/// Workflow module DbContext, schema <c>workflow</c> (ADR-0011, ADR-0017, ADR-0028). An approval instance is stored as an
/// append-only log of the actions it accepted, its history, and a summary row for queries and concurrency (DL-072a).
/// Loading replays the log through the engine and must reproduce the stored history exactly. The append-only and
/// continuity rules are also enforced by hand-written triggers in the WorkflowStorage migrations.
/// </summary>
public sealed class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "workflow";

    /// <summary>Bounds one action's stored inputs. A route of 64 levels with 256 people each fits well inside it.</summary>
    public const int MaxPayloadLength = 4_000_000;

    public override string Schema => SchemaName;

    public DbSet<ApprovalInstanceRow> Instances => Set<ApprovalInstanceRow>();

    public DbSet<ApprovalActionRow> Actions => Set<ApprovalActionRow>();

    public DbSet<ApprovalHistoryRow> History => Set<ApprovalHistoryRow>();

    public DbSet<ApprovalTaskRow> Tasks => Set<ApprovalTaskRow>();

    public DbSet<ApprovalDamageRow> Damage => Set<ApprovalDamageRow>();

    /// <summary>Events for other modules, written in each action's transaction (DL-076).</summary>
    public DbSet<OutboxMessageRow> Outbox => Set<OutboxMessageRow>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var instance = modelBuilder.Entity<ApprovalInstanceRow>();
        instance.ToTable("ApprovalInstance", t => t.HasTrigger("TR_ApprovalInstance_Guarded"));
        instance.HasKey(x => x.Id);
        instance.HasIndex(x => x.RequestItemId);
        instance.Property(x => x.State).IsRequired().IsCode(16);
        instance.Property(x => x.CorrelationId).IsCode(32).IsFixedLength();
        instance.Property(x => x.Requester).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        instance.Property(x => x.Beneficiary).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        instance.HasIndex(x => new { x.State, x.NextDeadlineUtc });

        var action = modelBuilder.Entity<ApprovalActionRow>();
        action.ToTable("ApprovalAction", t => t.HasTrigger("TR_ApprovalAction_AppendOnly"));
        action.IsAppendOnly();
        action.HasKey(x => new { x.InstanceId, x.Sequence });
        action.Property(x => x.Kind).IsRequired().IsCode(32);
        action.Property(x => x.Payload).IsRequired().HasMaxLength(MaxPayloadLength);
        action.Property(x => x.Actor).IsCode(AuditColumns.ActorMaxLength);
        action.HasOne<ApprovalInstanceRow>().WithMany().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Restrict);

        var history = modelBuilder.Entity<ApprovalHistoryRow>();
        history.ToTable("ApprovalHistory", t => t.HasTrigger("TR_ApprovalHistory_AppendOnly"));
        history.IsAppendOnly();
        history.HasKey(x => new { x.InstanceId, x.EventVersion });
        history.Property(x => x.Kind).IsRequired().IsCode(48);
        history.Property(x => x.Actor).IsCode(AuditColumns.ActorMaxLength);
        history.HasOne<ApprovalInstanceRow>().WithMany().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Restrict);

        var task = modelBuilder.Entity<ApprovalTaskRow>();
        task.ToTable("ApprovalTask");
        task.HasKey(x => new { x.InstanceId, x.StageId, x.Person });
        task.Property(x => x.Person).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        task.Property(x => x.Kind).IsRequired().IsCode(16);
        task.HasIndex(x => new { x.Person, x.DeadlineUtc });
        task.HasOne<ApprovalInstanceRow>().WithMany().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Restrict);

        var damage = modelBuilder.Entity<ApprovalDamageRow>();
        damage.ToTable("ApprovalDamage", t => t.HasTrigger("TR_ApprovalDamage_AppendOnly"));
        damage.IsAppendOnly();
        damage.HasKey(x => x.InstanceId);
        damage.HasOne<ApprovalInstanceRow>().WithMany().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.ConfigureOutbox("TR_OutboxMessage_Guarded");
    }
}

/// <summary>
/// An instance found damaged on load, recorded once so the damage is audited once however often it is read (DL-075b).
/// Never changed or deleted: repairing an instance is a reviewed operation of its own, not this table's business.
/// </summary>
public sealed class ApprovalDamageRow
{
    public required Guid InstanceId { get; init; }

    public required DateTimeOffset DetectedAtUtc { get; init; }
}

/// <summary>
/// One approval instance's summary: what queries and the timeout worker need, and the row that serialises writes. Every
/// value here is also derived again from the replayed log on load, and a mismatch refuses the instance.
/// </summary>
public sealed class ApprovalInstanceRow
{
    public required Guid Id { get; init; }

    public required Guid RequestItemId { get; init; }

    public required string Requester { get; init; }

    public required string Beneficiary { get; init; }

    public required string State { get; set; }

    /// <summary>The engine's history version: a caller acting on the instance names it.</summary>
    public required long EngineVersion { get; set; }

    public required int ActionCount { get; set; }

    public required bool ProvisioningAuthorized { get; set; }

    /// <summary>The earliest deadline of an open level, for the timeout worker (slice 3); null when nothing is open.</summary>
    public DateTimeOffset? NextDeadlineUtc { get; set; }

    /// <summary>The trace id the approval started under (the request's, DL-094); never changes. Null for older instances.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>One accepted action and its inputs, as JSON. Never changed or deleted.</summary>
public sealed class ApprovalActionRow
{
    public required Guid InstanceId { get; init; }

    /// <summary>1, 2, 3 and so on with no gaps; the database refuses anything else.</summary>
    public required int Sequence { get; init; }

    public required string Kind { get; init; }

    public required string Payload { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    /// <summary>Who acted, when a person did; null for the system (start, supplied approvers, timeouts).</summary>
    public string? Actor { get; init; }

    /// <summary>The engine's history version after this action.</summary>
    public required long VersionAfter { get; init; }
}

/// <summary>One engine history event, stored for queries and as evidence. Never changed or deleted.</summary>
public sealed class ApprovalHistoryRow
{
    public required Guid InstanceId { get; init; }

    /// <summary>The engine's history version of this event: 1, 2, 3 and so on. Not the shared row <c>Version</c> column (ADR-0017).</summary>
    public required long EventVersion { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    public required string Kind { get; init; }

    public Guid? StageId { get; init; }

    public string? Actor { get; init; }
}

/// <summary>
/// One person who can act on one open level now, for the inbox (DL-073b). Rebuilt for the instance in the same transaction
/// as every action. Only a lookup: acting still replays the instance and the engine decides, so a stale or changed row can
/// never let anyone act.
/// </summary>
public sealed class ApprovalTaskRow
{
    public required Guid InstanceId { get; init; }

    public required Guid StageId { get; init; }

    public required string Person { get; init; }

    public required Guid RequestItemId { get; init; }

    public required int Sequence { get; init; }

    public required string Kind { get; init; }

    public DateTimeOffset? DeadlineUtc { get; init; }
}
