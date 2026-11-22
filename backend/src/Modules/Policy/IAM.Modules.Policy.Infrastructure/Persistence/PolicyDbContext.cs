using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Policy.Domain.Matrix;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Policy.Infrastructure.Persistence;

/// <summary>
/// Policy module DbContext, schema <c>policy</c> (ADR-0011, ADR-0017). Stage 8 slice 3 adds the enterprise approval matrix,
/// its numbered versions and the requests that activate them (DL-090). Immutability and the second-person rules are also
/// enforced by hand-written triggers and CHECKs in the ApprovalMatrix migrations.
/// </summary>
public sealed class PolicyDbContext(DbContextOptions<PolicyDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "policy";

    /// <summary>A matrix document is bounded (4 MB) by the parser; the column is large text on both engines.</summary>
    public override string Schema => SchemaName;

    public DbSet<ApprovalMatrix> Matrices => Set<ApprovalMatrix>();

    public DbSet<MatrixVersion> MatrixVersions => Set<MatrixVersion>();

    public DbSet<MatrixChangeRequestRow> MatrixRequests => Set<MatrixChangeRequestRow>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var matrix = modelBuilder.Entity<ApprovalMatrix>();
        matrix.ToTable("ApprovalMatrix", t => t.HasTrigger("TR_ApprovalMatrix_Immutable"));
        matrix.HasKey(x => x.Id);
        matrix.Property(x => x.Key).IsRequired().IsCode(32);
        matrix.HasIndex(x => x.Key).IsUnique();
        matrix.Ignore(x => x.CurrentDraft);
        matrix.Ignore(x => x.CurrentActive);
        matrix.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.MatrixId).OnDelete(DeleteBehavior.Restrict);
        matrix.Navigation(x => x.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);

        var version = modelBuilder.Entity<MatrixVersion>();
        version.ToTable("MatrixVersion", t => t.HasTrigger("TR_MatrixVersion_Immutable"));
        version.HasKey(x => x.Id);
        version.HasIndex(x => new { x.MatrixId, x.VersionNumber }).IsUnique();
        version.Property(x => x.State).HasConversion<string>().IsRequired().IsCode(16);
        version.Property(x => x.Document).IsRequired();
        version.Property(x => x.ContentHash).IsRequired().IsCode(64).IsFixedLength();
        version.Property(x => x.CompiledHash).IsRequired().IsCode(64).IsFixedLength();
        version.Property(x => x.EditedBy).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        version.Property(x => x.Editors).HasMaxLength(4100).IsRequired();
        version.HasOne<MatrixChangeRequestRow>().WithMany().HasForeignKey(x => x.ActivatedByRequestId).OnDelete(DeleteBehavior.Restrict);

        var request = modelBuilder.Entity<MatrixChangeRequestRow>();
        request.ToTable("MatrixChangeRequest", t => t.HasTrigger("TR_MatrixChangeRequest_DecidedImmutable"));
        request.HasKey(x => x.Id);
        request.Property(x => x.ContentHash).IsRequired().IsCode(64).IsFixedLength();
        request.Property(x => x.CompiledHash).IsRequired().IsCode(64).IsFixedLength();
        request.Property(x => x.Maker).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        request.Property(x => x.Reason).IsRequired().HasMaxLength(1000);
        request.Property(x => x.State).IsRequired().IsCode(16);
        request.Property(x => x.Checker).IsCode(AuditColumns.ActorMaxLength);
        request.Property(x => x.DecisionReason).HasMaxLength(1000);
        request.HasOne<ApprovalMatrix>().WithMany().HasForeignKey(x => x.MatrixId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<MatrixVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Restrict);
        request.HasIndex(x => new { x.State, x.ProposedAtUtc });
    }
}

/// <summary>A stored matrix change request (DL-090d), rebuilt with <c>MatrixChangeRequest.Restore</c>.</summary>
public sealed class MatrixChangeRequestRow
{
    public required Guid Id { get; init; }

    public required Guid MatrixId { get; init; }

    public required Guid VersionId { get; init; }

    public required int VersionNumber { get; init; }

    public required string ContentHash { get; init; }

    public required string CompiledHash { get; init; }

    public required string Maker { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset ProposedAtUtc { get; init; }

    public required string State { get; set; }

    public string? Checker { get; set; }

    public string? DecisionReason { get; set; }

    public DateTimeOffset? DecidedAtUtc { get; set; }
}
