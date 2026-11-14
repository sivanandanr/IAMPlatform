using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Organization.Domain.Workgroups;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Organization.Infrastructure.Persistence;

/// <summary>
/// Organization module DbContext, schema <c>organization</c> (ADR-0011, ADR-0017). Stage 4 adds IAM workgroups, their
/// numbered member lists and the requests that activate them (DL-068). Immutability and approval rules are also enforced
/// by hand-written triggers and CHECKs in the Workgroups migrations.
/// </summary>
public sealed class OrganizationDbContext(DbContextOptions<OrganizationDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "organization";

    public override string Schema => SchemaName;

    public DbSet<Workgroup> Workgroups => Set<Workgroup>();

    public DbSet<WorkgroupVersion> WorkgroupVersions => Set<WorkgroupVersion>();

    public DbSet<WorkgroupChangeRequestRow> ChangeRequests => Set<WorkgroupChangeRequestRow>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var workgroup = modelBuilder.Entity<Workgroup>();
        workgroup.ToTable("Workgroup", t => t.HasTrigger("TR_Workgroup_KeyImmutable"));
        workgroup.HasKey(x => x.Id);
        workgroup.Property(x => x.Key).IsRequired().IsCode(WorkgroupRules.KeyMaxLength);
        workgroup.HasIndex(x => x.Key).IsUnique();
        workgroup.Property(x => x.DisplayName).IsRequired().HasMaxLength(WorkgroupRules.DisplayNameMaxLength);
        workgroup.Ignore(x => x.CurrentDraft);
        workgroup.Ignore(x => x.CurrentActive);
        workgroup.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.WorkgroupId).OnDelete(DeleteBehavior.Restrict);
        workgroup.Navigation(x => x.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);

        var version = modelBuilder.Entity<WorkgroupVersion>();
        version.ToTable("WorkgroupVersion", t => t.HasTrigger("TR_WorkgroupVersion_Immutable"));
        version.HasKey(x => x.Id);
        version.HasIndex(x => new { x.WorkgroupId, x.VersionNumber }).IsUnique();
        version.Property(x => x.State).HasConversion<string>().IsRequired().IsCode(16);
        version.Property(x => x.ContentHash).IsRequired().IsCode(WorkgroupRules.HashLength).IsFixedLength();
        version.Ignore(x => x.People);
        version.HasOne<WorkgroupChangeRequestRow>().WithMany().HasForeignKey(x => x.ActivatedByRequestId).OnDelete(DeleteBehavior.Restrict);
        version.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Cascade);
        version.Navigation(x => x.Members).UsePropertyAccessMode(PropertyAccessMode.Field);

        var member = modelBuilder.Entity<WorkgroupMember>();
        member.ToTable("WorkgroupMember", t => t.HasTrigger("TR_WorkgroupMember_DraftOnly"));
        member.HasKey(x => new { x.VersionId, x.Person });
        member.Property(x => x.Person).IsRequired().IsCode(AuditColumns.ActorMaxLength);

        var request = modelBuilder.Entity<WorkgroupChangeRequestRow>();
        request.ToTable("WorkgroupChangeRequest", t => t.HasTrigger("TR_WorkgroupChangeRequest_DecidedImmutable"));
        request.HasKey(x => x.Id);
        request.Property(x => x.ContentHash).IsRequired().IsCode(WorkgroupRules.HashLength).IsFixedLength();
        request.Property(x => x.Maker).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        request.Property(x => x.Reason).IsRequired().HasMaxLength(1000);
        request.Property(x => x.State).IsRequired().IsCode(16);
        request.Property(x => x.Checker).IsCode(AuditColumns.ActorMaxLength);
        request.Property(x => x.DecisionReason).HasMaxLength(1000);
        request.HasOne<Workgroup>().WithMany().HasForeignKey(x => x.WorkgroupId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<WorkgroupVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Restrict);
        request.HasIndex(x => new { x.State, x.ProposedAtUtc });
    }
}

/// <summary>A stored member change request. The domain object is rebuilt from it with <c>WorkgroupChangeRequest.Restore</c>.</summary>
public sealed class WorkgroupChangeRequestRow
{
    public required Guid Id { get; init; }

    public required Guid WorkgroupId { get; init; }

    public required Guid VersionId { get; init; }

    public required int VersionNumber { get; init; }

    public required string ContentHash { get; init; }

    public required string Maker { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset ProposedAtUtc { get; init; }

    public required string State { get; set; }

    public string? Checker { get; set; }

    public string? DecisionReason { get; set; }

    public DateTimeOffset? DecidedAtUtc { get; set; }
}
