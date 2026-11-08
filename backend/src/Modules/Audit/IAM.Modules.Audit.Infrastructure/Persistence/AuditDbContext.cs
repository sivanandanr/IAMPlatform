using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Application;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Audit.Infrastructure.Persistence;

/// <summary>
/// Audit module DbContext, schema <c>audit</c> (ADR-0017, ADR-0018). It defines the append-only
/// <c>audit.AuditEvent</c> table for migrations and for audit readers. Writes never go through this context: they use
/// <see cref="AuditWriter"/> on the caller's own transaction.
/// </summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "audit";

    /// <summary>
    /// Role that may only insert audit rows (every column except <c>Seq</c> on PostgreSQL); created by
    /// <c>infra/db/*/grant-audit-role.sql</c>. Granted to every module runtime login.
    /// </summary>
    public const string WriterRole = "iam_audit_writer";

    /// <summary>Role that may only read audit rows; created by <c>infra/db/*/grant-audit-role.sql</c>. Granted to the audit login.</summary>
    public const string ReaderRole = "iam_audit_reader";

    public override string Schema => SchemaName;

    public DbSet<AuditEventRow> Events => Set<AuditEventRow>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var e = modelBuilder.Entity<AuditEventRow>();
        e.ToTable("AuditEvent");
        e.IsAppendOnly();

        // RecordedAtUtc leads the key so PostgreSQL can later partition by time (ADR-0018).
        e.HasKey(x => new { x.RecordedAtUtc, x.Id });
        e.HasIndex(x => x.Id).IsUnique();

        // Database-assigned order of arrival. Not gap-free: a rolled-back insert still uses a number.
        e.Property(x => x.Seq).ValueGeneratedOnAdd();
        e.HasIndex(x => x.Seq).IsUnique();

        e.Property(x => x.ActorId).IsRequired().HasMaxLength(AuditEventValidator.ActorMaxLength);
        e.Property(x => x.EffectiveActorId).HasMaxLength(AuditEventValidator.ActorMaxLength);
        e.Property(x => x.Action).IsRequired().IsCode(AuditEventValidator.ActionMaxLength);
        e.Property(x => x.TargetType).IsRequired().IsCode(AuditEventValidator.TargetTypeMaxLength);
        e.Property(x => x.TargetId).HasMaxLength(AuditEventValidator.TargetIdMaxLength);
        e.Property(x => x.Outcome).IsRequired().IsCode(AuditEventValidator.OutcomeMaxLength);
        e.Property(x => x.Reason).HasMaxLength(AuditEventValidator.ReasonMaxLength);
        e.Property(x => x.CorrelationId).IsRequired().IsCode(AuditEventValidator.CorrelationIdMaxLength);
        e.Property(x => x.CausationId).IsCode(AuditEventValidator.CorrelationIdMaxLength);
        e.Property(x => x.PolicyVersionRef).HasMaxLength(AuditEventValidator.PolicyVersionRefMaxLength);
        e.Property(x => x.PolicyVersionHash).IsCode(AuditEventValidator.HashLength).IsFixedLength();
        e.Property(x => x.PayloadCanonical).IsRequired();
        e.Property(x => x.RowHash).IsRequired().IsCode(AuditEventValidator.HashLength).IsFixedLength();

        e.HasIndex(x => x.CorrelationId);
        e.HasIndex(x => new { x.TargetType, x.TargetId });
    }
}

/// <summary>One stored audit event. Persistence shape only; rows are inserted by <see cref="AuditWriter"/> and never changed.</summary>
public sealed class AuditEventRow
{
    public Guid Id { get; set; }

    public long Seq { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public string ActorId { get; set; } = string.Empty;

    public string? EffectiveActorId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string TargetType { get; set; } = string.Empty;

    public string? TargetId { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public string? CausationId { get; set; }

    public string? PolicyVersionRef { get; set; }

    public string? PolicyVersionHash { get; set; }

    public string PayloadCanonical { get; set; } = string.Empty;

    /// <summary>SHA-256 over every other stored field except <c>Seq</c> (<c>AuditEventValidator.ComputeRowHash</c>).</summary>
    public string RowHash { get; set; } = string.Empty;

    public int SchemaVersion { get; set; }
}
