using IAM.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Persistence.Tests.Probe;

/// <summary>
/// Test-only module (schema <c>parity_probe</c>) that exercises every S1-06 convention on both engines. It is never
/// part of a product migration; parity tests create its tables directly from the model.
/// </summary>
public sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "parity_probe";

    public override string Schema => SchemaName;

    public DbSet<ProbeItem> Items => Set<ProbeItem>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<ProbeItem>();
        item.ToTable("ProbeItems");
        item.HasKey(x => x.Id);
        item.Property(x => x.Code).IsRequired().IsCode(32);
        item.HasIndex(x => x.Code).IsUnique();
        item.Property(x => x.ExternalRef).IsCode(64);
        item.HasIndex(x => x.ExternalRef).IsUnique();
        item.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
    }
}

public sealed class ProbeItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Code { get; set; } = string.Empty;

    public string? ExternalRef { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset EffectiveAtUtc { get; set; }

    public decimal Amount { get; set; }
}
