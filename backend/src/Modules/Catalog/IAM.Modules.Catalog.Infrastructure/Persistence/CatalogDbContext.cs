using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Catalog.Domain.Applications;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Catalog.Infrastructure.Persistence;

/// <summary>
/// Catalog module DbContext, schema <c>catalog</c> (ADR-0011, ADR-0017). Stage 3 adds applications, their numbered settings
/// versions (owner and application roles), the requests that activate them, and application role types (DL-066).
/// Immutability and approval rules are also enforced by hand-written triggers and CHECKs in the ApplicationCatalog
/// migrations; the trigger names are declared here so EF Core does not use OUTPUT without INTO on SQL Server.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "catalog";

    public override string Schema => SchemaName;

    public DbSet<Domain.Applications.Application> Applications => Set<Domain.Applications.Application>();

    public DbSet<ApplicationSettingsVersion> SettingsVersions => Set<ApplicationSettingsVersion>();

    public DbSet<ApplicationSettingsRequestRow> SettingsRequests => Set<ApplicationSettingsRequestRow>();

    public DbSet<ApplicationRoleType> RoleTypes => Set<ApplicationRoleType>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var application = modelBuilder.Entity<Domain.Applications.Application>();
        application.ToTable("Application", t => t.HasTrigger("TR_Application_KeyImmutable"));
        application.HasKey(x => x.Id);
        application.Property(x => x.Key).IsRequired().IsCode(CatalogRules.KeyMaxLength);
        application.HasIndex(x => x.Key).IsUnique();
        application.Property(x => x.DisplayName).IsRequired().HasMaxLength(CatalogRules.DisplayNameMaxLength);
        application.Ignore(x => x.CurrentDraft);
        application.Ignore(x => x.CurrentActive);
        application.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        application.Navigation(x => x.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);

        var version = modelBuilder.Entity<ApplicationSettingsVersion>();
        version.ToTable("ApplicationSettingsVersion", t => t.HasTrigger("TR_ApplicationSettingsVersion_Immutable"));
        version.HasKey(x => x.Id);
        version.HasIndex(x => new { x.ApplicationId, x.VersionNumber }).IsUnique();
        version.Property(x => x.State).HasConversion<string>().IsRequired().IsCode(16);
        version.Property(x => x.Owner).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        version.Property(x => x.ContentHash).IsRequired().IsCode(CatalogRules.HashLength).IsFixedLength();
        version.Ignore(x => x.HasEmptyRequiredRole);
        version.HasOne<ApplicationSettingsRequestRow>().WithMany().HasForeignKey(x => x.ActivatedByRequestId).OnDelete(DeleteBehavior.Restrict);
        version.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.SettingsVersionId).OnDelete(DeleteBehavior.Cascade);
        version.Navigation(x => x.Roles).UsePropertyAccessMode(PropertyAccessMode.Field);

        var role = modelBuilder.Entity<ApplicationSettingsRole>();
        role.ToTable("ApplicationSettingsRole", t => t.HasTrigger("TR_ApplicationSettingsRole_DraftOnly"));
        role.HasKey(x => new { x.SettingsVersionId, x.RoleTypeId });
        role.Property(x => x.Requirement).HasConversion<string>().IsRequired().IsCode(16);
        role.Property(x => x.HolderKind).HasConversion<string>().IsCode(16);
        role.Property(x => x.HolderId).IsCode(AuditColumns.ActorMaxLength);
        role.HasOne<ApplicationRoleType>().WithMany().HasForeignKey(x => x.RoleTypeId).OnDelete(DeleteBehavior.Restrict);

        var request = modelBuilder.Entity<ApplicationSettingsRequestRow>();
        request.ToTable("ApplicationSettingsRequest", t => t.HasTrigger("TR_ApplicationSettingsRequest_DecidedImmutable"));
        request.HasKey(x => x.Id);
        request.Property(x => x.ContentHash).IsRequired().IsCode(CatalogRules.HashLength).IsFixedLength();
        request.Property(x => x.ProposedOwner).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        request.Property(x => x.Maker).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        request.Property(x => x.Reason).IsRequired().HasMaxLength(1000);
        request.Property(x => x.State).IsRequired().IsCode(16);
        request.Property(x => x.Checker).IsCode(AuditColumns.ActorMaxLength);
        request.Property(x => x.DecisionReason).HasMaxLength(1000);
        request.HasOne<Domain.Applications.Application>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<ApplicationSettingsVersion>().WithMany().HasForeignKey(x => x.SettingsVersionId).OnDelete(DeleteBehavior.Restrict);
        request.HasIndex(x => new { x.State, x.ProposedAtUtc });

        var type = modelBuilder.Entity<ApplicationRoleType>();
        type.ToTable("ApplicationRoleType", t => t.HasTrigger("TR_ApplicationRoleType_Guarded"));
        type.HasKey(x => x.Id);
        type.Property(x => x.Code).IsRequired().IsCode(CatalogRules.KeyMaxLength);
        type.Property(x => x.DisplayName).IsRequired().HasMaxLength(CatalogRules.RoleTypeDisplayNameMaxLength);
        type.Property(x => x.State).HasConversion<string>().IsRequired().IsCode(16);
        type.Property(x => x.Maker).IsCode(AuditColumns.ActorMaxLength);
        type.Property(x => x.Reason).HasMaxLength(1000);
        type.Property(x => x.Checker).IsCode(AuditColumns.ActorMaxLength);
        type.Property(x => x.DecisionReason).HasMaxLength(1000);
        type.Ignore(x => x.IsUsable);
        type.Ignore(x => x.HoldsCode);
        type.HasIndex(x => x.Code);
    }
}

/// <summary>A stored settings request (DL-066). The domain object is rebuilt from it with <c>ApplicationSettingsRequest.Restore</c>.</summary>
public sealed class ApplicationSettingsRequestRow
{
    public required Guid Id { get; init; }

    public required Guid ApplicationId { get; init; }

    public required Guid SettingsVersionId { get; init; }

    public required int VersionNumber { get; init; }

    public required string ContentHash { get; init; }

    public required string ProposedOwner { get; init; }

    public required string Maker { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset ProposedAtUtc { get; init; }

    public required string State { get; set; }

    public string? Checker { get; set; }

    public string? DecisionReason { get; set; }

    public DateTimeOffset? DecidedAtUtc { get; set; }
}
