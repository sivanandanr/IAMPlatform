using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Request.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Request.Infrastructure.Persistence;

/// <summary>
/// Request module DbContext, schema <c>request</c> (ADR-0011, ADR-0017). Stage 8 slice 4a adds frozen request items and the
/// module's outbox (ADR-0006, DL-091). Items never change once frozen: hand-written triggers in the RequestItems migrations
/// refuse it, and refuse deleting them.
/// </summary>
public sealed class RequestDbContext(DbContextOptions<RequestDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "request";

    public override string Schema => SchemaName;

    public DbSet<RequestItem> Items => Set<RequestItem>();

    public DbSet<OutboxMessageRow> Outbox => Set<OutboxMessageRow>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<RequestItem>();
        item.ToTable("RequestItem", t => t.HasTrigger("TR_RequestItem_Frozen"));
        item.HasKey(x => x.Id);
        item.Property(x => x.Requester).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        item.Property(x => x.Beneficiary).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        item.Property(x => x.EntitlementContentHash).IsRequired().IsCode(64).IsFixedLength();
        item.Property(x => x.NewAccountType).IsCode(16);
        item.Property(x => x.NewAccountName).IsCode(256);
        item.Property(x => x.State).HasConversion<string>().IsRequired().IsCode(16);
        item.Property(x => x.MatrixContentHash).IsRequired().IsCode(64).IsFixedLength();
        item.Property(x => x.PolicyMaker).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        item.Property(x => x.Evidence).IsRequired();
        item.Property(x => x.Route).IsRequired();
        item.HasIndex(x => new { x.Requester, x.SubmittedAtUtc });
        item.HasIndex(x => new { x.Beneficiary, x.SubmittedAtUtc });

        // One waiting item per person and entitlement: a filtered unique index, hand-written in the migrations.
        modelBuilder.ConfigureOutbox("TR_OutboxMessage_Guarded");
    }
}
