using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Identity.Domain.Attributes;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Identity module DbContext, schema <c>identity</c> (ADR-0017). S1-08 adds the shared attribute catalog
/// (ADR-0015, DL-005): <c>AttributeDefinition</c>, <c>AttributeDefinitionVersion</c> and
/// <c>AttributeDefinitionVersionOperator</c>; stage 2 adds <c>AttributePublishRequest</c> (DL-007, DL-064). Attribute values
/// and identity snapshots arrive later.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "identity";

    public override string Schema => SchemaName;

    public DbSet<AttributeDefinition> AttributeDefinitions => Set<AttributeDefinition>();

    public DbSet<AttributeDefinitionVersion> AttributeDefinitionVersions => Set<AttributeDefinitionVersion>();

    public DbSet<AttributePublishRequestRow> AttributePublishRequests => Set<AttributePublishRequestRow>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var definition = modelBuilder.Entity<AttributeDefinition>();
        // Immutability triggers are added by hand in the AttributeCatalog migrations (ADR-0015, IAM-WF-019). Declaring
        // them tells EF Core not to use OUTPUT without INTO on SQL Server, which triggers do not allow.
        definition.ToTable("AttributeDefinition", t => t.HasTrigger("TR_AttributeDefinition_KeyImmutable"));
        definition.HasKey(x => x.Id);
        definition.Property(x => x.Key).IsRequired().IsCode(AttributeRules.KeyMaxLength);
        definition.HasIndex(x => x.Key).IsUnique();
        definition.Ignore(x => x.CurrentDraft);
        definition.Ignore(x => x.CurrentPublished);
        definition.HasMany(x => x.Versions)
            .WithOne()
            .HasForeignKey(x => x.AttributeDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);
        definition.Navigation(x => x.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);

        var version = modelBuilder.Entity<AttributeDefinitionVersion>();
        version.ToTable("AttributeDefinitionVersion", t => t.HasTrigger("TR_AttributeDefinitionVersion_Immutable"));
        version.HasKey(x => x.Id);
        version.HasIndex(x => new { x.AttributeDefinitionId, x.VersionNumber }).IsUnique();
        version.Property(x => x.State).HasConversion<string>().IsRequired().IsCode(16);
        version.Property(x => x.DisplayName).IsRequired().HasMaxLength(AttributeRules.DisplayNameMaxLength);
        version.Property(x => x.DataType).HasConversion<string>().IsRequired().IsCode(16);
        version.Property(x => x.SubjectScope).HasConversion<string>().IsRequired().IsCode(16);
        version.Property(x => x.SourceSystem).IsRequired().IsCode(AttributeRules.SourceSystemMaxLength);
        version.Property(x => x.Sensitivity).HasConversion<string>().IsRequired().IsCode(16);
        version.Property(x => x.Normalization).HasConversion<string>().IsRequired().IsCode(32);
        version.Property(x => x.ContentHash).IsRequired().IsCode(AttributeRules.HashLength).IsFixedLength();

        // DL-064: a published or retired version names the approved request that published it; the database checks the
        // link and that the request was approved (hand-written in the AttributePublishing migrations).
        version.HasOne<AttributePublishRequestRow>().WithMany().HasForeignKey(x => x.PublishedByRequestId).OnDelete(DeleteBehavior.Restrict);
        version.Ignore(x => x.OperatorList);
        version.HasMany(x => x.Operators)
            .WithOne()
            .HasForeignKey(x => x.AttributeDefinitionVersionId)
            .OnDelete(DeleteBehavior.Cascade); // a draft's removed operators are deleted; triggers protect non-draft versions
        version.Navigation(x => x.Operators).UsePropertyAccessMode(PropertyAccessMode.Field);

        var request = modelBuilder.Entity<AttributePublishRequestRow>();
        request.ToTable("AttributePublishRequest", t => t.HasTrigger("TR_AttributePublishRequest_DecidedImmutable"));
        request.HasKey(x => x.Id);
        request.Property(x => x.ContentHash).IsRequired().IsCode(AttributeRules.HashLength).IsFixedLength();
        request.Property(x => x.Maker).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        request.Property(x => x.Reason).IsRequired().HasMaxLength(1000);
        request.Property(x => x.State).IsRequired().IsCode(16);
        request.Property(x => x.Checker).IsCode(AuditColumns.ActorMaxLength);
        request.Property(x => x.DecisionReason).HasMaxLength(1000);
        request.HasOne<AttributeDefinition>().WithMany().HasForeignKey(x => x.AttributeDefinitionId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<AttributeDefinitionVersion>().WithMany().HasForeignKey(x => x.AttributeDefinitionVersionId).OnDelete(DeleteBehavior.Restrict);
        request.HasIndex(x => new { x.State, x.ProposedAtUtc });

        var op = modelBuilder.Entity<AttributeDefinitionVersionOperator>();
        op.ToTable("AttributeDefinitionVersionOperator", t => t.HasTrigger("TR_AttributeDefinitionVersionOperator_Immutable"));
        op.HasKey(x => new { x.AttributeDefinitionVersionId, x.Operator });
        op.Property(x => x.Operator).HasConversion<string>().IsRequired().IsCode(32);
    }
}

/// <summary>A stored publish request (DL-064). The domain object is rebuilt from it with <c>AttributePublishRequest.Restore</c>.</summary>
public sealed class AttributePublishRequestRow
{
    public required Guid Id { get; init; }

    public required Guid AttributeDefinitionId { get; init; }

    public required Guid AttributeDefinitionVersionId { get; init; }

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
