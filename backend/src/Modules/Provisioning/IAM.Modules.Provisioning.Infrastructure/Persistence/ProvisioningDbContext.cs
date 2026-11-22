using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Provisioning.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Provisioning.Infrastructure.Persistence;

/// <summary>
/// Provisioning module DbContext, schema <c>provisioning</c> (ADR-0011, ADR-0017, DL-093): authorised items, their planned
/// operations, the durable operation ledger and the inbox of authorisations received. Hand-written triggers in the
/// ProvisioningItems migrations keep what an item was planned from unchanged, let states only move forward, and keep the
/// ledger and inbox append-only.
/// </summary>
public sealed class ProvisioningDbContext(DbContextOptions<ProvisioningDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "provisioning";

    public override string Schema => SchemaName;

    public DbSet<ProvisioningItem> Items => Set<ProvisioningItem>();

    public DbSet<ProvisioningOperation> Operations => Set<ProvisioningOperation>();

    public DbSet<OperationLedgerRecord> Ledger => Set<OperationLedgerRecord>();

    public DbSet<InboxMessageRow> Inbox => Set<InboxMessageRow>();

    public DbSet<GrantLock> GrantLocks => Set<GrantLock>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<ProvisioningItem>();
        item.ToTable("ProvisioningItem", t => t.HasTrigger("TR_ProvisioningItem_Guarded"));
        item.HasKey(x => x.Id);
        item.Property(x => x.Id).ValueGeneratedNever();
        item.Property(x => x.State).HasConversion<string>().IsRequired().IsCode(16);
        item.Property(x => x.Problem).IsCode(32);
        item.Property(x => x.IntegrityProblem).IsCode(32);
        item.Property(x => x.Beneficiary).IsCode(AuditColumns.ActorMaxLength);
        item.Property(x => x.NewAccountType).IsCode(16);
        item.Property(x => x.AccountName).IsCode(256);
        item.Property(x => x.ConnectorKey).IsCode(32);
        item.Property(x => x.CorrelationId).IsCode(32).IsFixedLength();
        item.Property(x => x.UndoReason).IsCode(32);
        item.HasIndex(x => new { x.State, x.ReceivedAtUtc });
        item.HasIndex(x => new { x.AccountId, x.EntitlementId });

        var operation = modelBuilder.Entity<ProvisioningOperation>();
        operation.ToTable("ProvisioningOperation", t => t.HasTrigger("TR_ProvisioningOperation_Guarded"));
        operation.HasKey(x => x.Id);
        operation.Property(x => x.Id).ValueGeneratedNever();
        operation.Property(x => x.Action).IsRequired().IsCode(64);
        operation.Property(x => x.TargetType).IsRequired().IsCode(32);
        operation.Property(x => x.TargetId).IsRequired().HasMaxLength(1024);
        operation.Property(x => x.RelatedType).IsCode(32);
        operation.Property(x => x.RelatedId).HasMaxLength(1024);
        operation.Property(x => x.Attributes).IsRequired().HasMaxLength(4000);
        operation.Property(x => x.State).HasConversion<string>().IsRequired().IsCode(16);
        operation.Property(x => x.ResultCode).IsCode(32);
        operation.HasOne<ProvisioningItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        operation.HasIndex(x => new { x.ItemId, x.Step }).IsUnique();

        var ledger = modelBuilder.Entity<OperationLedgerRecord>();
        ledger.ToTable("OperationLedger", t => t.HasTrigger("TR_OperationLedger_AppendOnly"));
        ledger.IsAppendOnly();
        ledger.HasKey(x => x.OperationId);
        ledger.Property(x => x.OperationId).ValueGeneratedNever();
        ledger.Property(x => x.Fingerprint).IsRequired().IsCode(OperationLedgerRecord.FingerprintLength).IsFixedLength();
        ledger.Property(x => x.Status).IsRequired().IsCode(OperationLedgerRecord.StatusMaxLength);
        ledger.Property(x => x.Code).IsCode(OperationLedgerRecord.CodeMaxLength);

        var grantLock = modelBuilder.Entity<GrantLock>();
        grantLock.ToTable("GrantLock");
        grantLock.HasKey(x => new { x.AccountId, x.EntitlementId });

        modelBuilder.ConfigureInbox("TR_InboxMessage_AppendOnly");
    }
}
