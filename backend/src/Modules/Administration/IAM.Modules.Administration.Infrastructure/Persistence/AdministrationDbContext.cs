using IAM.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Administration.Infrastructure.Persistence;

/// <summary>
/// Administration module DbContext, schema <c>configuration</c> (ADR-0011, ADR-0017, DL-059j). Stage 2 adds access
/// control: the current roles, their actions, assignments and incompatible pairs; an append-only history of model
/// versions; and the change requests that produce them. Decided change requests and model versions never change,
/// enforced by triggers in the AccessControl migrations.
/// </summary>
public sealed class AdministrationDbContext(DbContextOptions<AdministrationDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "configuration";
    public const int RoleCodeLength = 64;
    public const int ActionIdLength = 100;
    public const int SubjectLength = AuditColumns.ActorMaxLength;
    /// <summary><c>v1:</c> plus 64 lower-case hex characters (<c>AccessModel.HashScheme</c>).</summary>
    public const int HashLength = 67;
    public const int KindLength = 32;
    public const int StateLength = 16;
    public const int PayloadLength = 4_000;
    public const int ReasonLength = 1_000;
    public const int RoleNameLength = 200;

    public override string Schema => SchemaName;

    public DbSet<AccessRoleRow> Roles => Set<AccessRoleRow>();

    public DbSet<AccessRoleActionRow> RoleActions => Set<AccessRoleActionRow>();

    public DbSet<AccessRoleAssignmentRow> Assignments => Set<AccessRoleAssignmentRow>();

    public DbSet<AccessIncompatibleRolePairRow> IncompatiblePairs => Set<AccessIncompatibleRolePairRow>();

    public DbSet<AccessModelVersionRow> ModelVersions => Set<AccessModelVersionRow>();

    public DbSet<AccessChangeRequestRow> ChangeRequests => Set<AccessChangeRequestRow>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var role = modelBuilder.Entity<AccessRoleRow>();
        role.ToTable("AccessRole");
        role.HasKey(x => x.Code);
        role.Property(x => x.Code).IsCode(RoleCodeLength);
        role.Property(x => x.Name).IsRequired().HasMaxLength(RoleNameLength);

        var action = modelBuilder.Entity<AccessRoleActionRow>();
        action.ToTable("AccessRoleAction");
        action.HasKey(x => new { x.RoleCode, x.ActionId });
        action.Property(x => x.RoleCode).IsCode(RoleCodeLength);
        action.Property(x => x.ActionId).IsCode(ActionIdLength);
        action.HasOne<AccessRoleRow>().WithMany().HasForeignKey(x => x.RoleCode).OnDelete(DeleteBehavior.Cascade);

        var assignment = modelBuilder.Entity<AccessRoleAssignmentRow>();
        assignment.ToTable("AccessRoleAssignment");
        assignment.HasKey(x => new { x.Subject, x.RoleCode });
        assignment.Property(x => x.Subject).IsCode(SubjectLength);
        assignment.Property(x => x.RoleCode).IsCode(RoleCodeLength);
        assignment.HasOne<AccessRoleRow>().WithMany().HasForeignKey(x => x.RoleCode).OnDelete(DeleteBehavior.Restrict);
        assignment.HasIndex(x => x.RoleCode);

        var pair = modelBuilder.Entity<AccessIncompatibleRolePairRow>();
        pair.ToTable("AccessIncompatibleRolePair");
        pair.HasKey(x => new { x.First, x.Second });
        pair.Property(x => x.First).IsCode(RoleCodeLength);
        pair.Property(x => x.Second).IsCode(RoleCodeLength);
        pair.HasOne<AccessRoleRow>().WithMany().HasForeignKey(x => x.First).OnDelete(DeleteBehavior.Restrict);
        pair.HasOne<AccessRoleRow>().WithMany().HasForeignKey(x => x.Second).OnDelete(DeleteBehavior.Restrict);

        // One row per applied version. The primary key is what stops two approvals at once both applying: each inserts the
        // next version number, and only one can.
        var version = modelBuilder.Entity<AccessModelVersionRow>();
        version.ToTable("AccessModelVersion", t =>
        {
            t.HasTrigger("TR_AccessModelVersion_AppendOnly");
            t.HasTrigger("TR_AccessModelVersion_ClaimsProposed");
        });
        version.IsAppendOnly();
        version.HasKey(x => x.ModelVersion);
        version.Property(x => x.ModelVersion).ValueGeneratedNever();
        version.Property(x => x.Kind).IsCode(KindLength);
        version.Property(x => x.ModelHash).IsCode(HashLength);
        version.Property(x => x.AppliedBy).IsRequired().HasMaxLength(AuditColumns.ActorMaxLength);
        version.HasIndex(x => x.ChangeRequestId).IsUnique().HasFilter(null);

        // Each change version names a real request. The reverse link (an approved request names the version it produced,
        // matching on both columns) and the "only a proposed request can claim a version" trigger are in the migration.
        version.HasOne<AccessChangeRequestRow>().WithMany().HasForeignKey(x => x.ChangeRequestId).OnDelete(DeleteBehavior.Restrict);

        var change = modelBuilder.Entity<AccessChangeRequestRow>();
        change.ToTable("AccessChangeRequest", t => t.HasTrigger("TR_AccessChangeRequest_DecidedImmutable"));
        change.HasKey(x => x.Id);
        change.Property(x => x.Kind).IsCode(KindLength);
        change.Property(x => x.Payload).IsRequired().HasMaxLength(PayloadLength);
        change.Property(x => x.Maker).IsCode(SubjectLength);
        change.Property(x => x.Reason).IsRequired().HasMaxLength(ReasonLength);
        change.Property(x => x.State).IsCode(StateLength);
        change.Property(x => x.Checker).IsCode(SubjectLength);
        change.Property(x => x.DecisionReason).HasMaxLength(ReasonLength);
        change.HasIndex(x => new { x.State, x.ProposedAtUtc });
    }
}

public sealed class AccessRoleRow
{
    public required string Code { get; init; }

    public required string Name { get; set; }

    public required bool Assignable { get; set; }
}

public sealed class AccessRoleActionRow
{
    public required string RoleCode { get; init; }

    public required string ActionId { get; init; }
}

public sealed class AccessRoleAssignmentRow
{
    public required string Subject { get; init; }

    public required string RoleCode { get; init; }
}

public sealed class AccessIncompatibleRolePairRow
{
    public required string First { get; init; }

    public required string Second { get; init; }
}

/// <summary>An applied model version: by which change (none for the first, bootstrap version), who applied it and when.</summary>
public sealed class AccessModelVersionRow
{
    public const string KindBootstrap = "Bootstrap";
    public const string KindChange = "Change";

    /// <summary>Named so it can't be mistaken for the row-version audit column, which append-only tables don't have.</summary>
    public required long ModelVersion { get; init; }

    public required string Kind { get; init; }

    public Guid? ChangeRequestId { get; init; }

    /// <summary>SHA-256 of the whole model this version produced (<c>AccessModel.ContentHash</c>); every read checks it.</summary>
    public required string ModelHash { get; init; }

    public required string AppliedBy { get; init; }

    public required DateTimeOffset AppliedAtUtc { get; init; }
}

public sealed class AccessChangeRequestRow
{
    public required Guid Id { get; init; }

    public required string Kind { get; init; }

    public required string Payload { get; init; }

    public required string Maker { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset ProposedAtUtc { get; init; }

    public required long ProposedAgainstVersion { get; init; }

    public required string State { get; set; }

    public string? Checker { get; set; }

    public string? DecisionReason { get; set; }

    public DateTimeOffset? DecidedAtUtc { get; set; }

    public long? AppliedAsVersion { get; set; }
}
