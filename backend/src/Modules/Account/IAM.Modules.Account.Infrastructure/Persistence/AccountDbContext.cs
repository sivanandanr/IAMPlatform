using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Account.Domain.Accounts;
using IAM.Modules.Account.Domain.Naming;
using Microsoft.EntityFrameworkCore;
using AccountEntity = IAM.Modules.Account.Domain.Accounts.Account;

namespace IAM.Modules.Account.Infrastructure.Persistence;

/// <summary>
/// Account module DbContext, schema <c>account</c> (ADR-0011, ADR-0017). Stage 8 slice 2 adds accounts, the requests that
/// link existing accounts to people, and naming rules with their versions and change requests (DL-089). Uniqueness of live
/// accounts and the second-person rules are also enforced by hand-written indexes, CHECKs and triggers in the Accounts
/// migrations; the trigger names are declared here so EF Core does not use OUTPUT without INTO on SQL Server.
/// </summary>
public sealed class AccountDbContext(DbContextOptions<AccountDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "account";

    public override string Schema => SchemaName;

    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();

    public DbSet<AccountLinkRequestRow> LinkRequests => Set<AccountLinkRequestRow>();

    public DbSet<NamingRule> NamingRules => Set<NamingRule>();

    public DbSet<NamingRuleVersion> NamingRuleVersions => Set<NamingRuleVersion>();

    public DbSet<NamingRuleChangeRequestRow> NamingRequests => Set<NamingRuleChangeRequestRow>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var account = modelBuilder.Entity<AccountEntity>();
        account.ToTable("Account", t => t.HasTrigger("TR_Account_Guarded"));
        account.HasKey(x => x.Id);
        account.Property(x => x.Type).HasConversion<string>().IsRequired().IsCode(16);
        account.Property(x => x.Name).IsRequired().IsCode(AccountRules.NameMaxLength);
        account.Property(x => x.NameKey).IsRequired().IsCode(AccountRules.NameMaxLength);
        account.Property(x => x.TargetId).HasMaxLength(AccountRules.TargetIdMaxLength);
        account.Property(x => x.TargetIdKey).IsCode(AccountRules.TargetIdMaxLength);
        account.Property(x => x.Owner).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        account.Property(x => x.Status).HasConversion<string>().IsRequired().IsCode(16);
        account.Property(x => x.Origin).HasConversion<string>().IsRequired().IsCode(16);
        account.HasOne<AccountLinkRequestRow>().WithMany().HasForeignKey(x => x.LinkRequestId).OnDelete(DeleteBehavior.Restrict);
        account.HasIndex(x => new { x.ApplicationId, x.Owner });
        account.HasIndex(x => x.ProvisioningItemId).IsUnique();

        // One Primary per person, unique live names and target ids: filtered unique indexes, hand-written in the migrations.
        var link = modelBuilder.Entity<AccountLinkRequestRow>();
        link.ToTable("AccountLinkRequest", t => t.HasTrigger("TR_AccountLinkRequest_DecidedImmutable"));
        link.HasKey(x => x.Id);
        link.Property(x => x.Type).IsRequired().IsCode(16);
        link.Property(x => x.Name).IsRequired().IsCode(AccountRules.NameMaxLength);
        link.Property(x => x.NameKey).IsRequired().IsCode(AccountRules.NameMaxLength);
        link.Property(x => x.TargetId).IsRequired().HasMaxLength(AccountRules.TargetIdMaxLength);
        link.Property(x => x.Owner).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        link.Property(x => x.Maker).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        link.Property(x => x.Reason).IsRequired().HasMaxLength(1000);
        link.Property(x => x.State).IsRequired().IsCode(16);
        link.Property(x => x.Checker).IsCode(AuditColumns.ActorMaxLength);
        link.Property(x => x.DecisionReason).HasMaxLength(1000);
        link.HasIndex(x => new { x.State, x.ProposedAtUtc });

        var rule = modelBuilder.Entity<NamingRule>();
        rule.ToTable("NamingRule", t => t.HasTrigger("TR_NamingRule_Immutable"));
        rule.HasKey(x => x.Id);
        rule.Property(x => x.AccountType).HasConversion<string>().IsRequired().IsCode(16);
        rule.HasIndex(x => new { x.ApplicationId, x.AccountType }).IsUnique();
        rule.Ignore(x => x.CurrentDraft);
        rule.Ignore(x => x.CurrentActive);
        rule.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.NamingRuleId).OnDelete(DeleteBehavior.Restrict);
        rule.Navigation(x => x.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);

        var version = modelBuilder.Entity<NamingRuleVersion>();
        version.ToTable("NamingRuleVersion", t => t.HasTrigger("TR_NamingRuleVersion_Immutable"));
        version.HasKey(x => x.Id);
        version.HasIndex(x => new { x.NamingRuleId, x.VersionNumber }).IsUnique();
        version.Property(x => x.State).HasConversion<string>().IsRequired().IsCode(16);
        version.Property(x => x.Template).IsRequired().HasMaxLength(NamingTemplates.MaxStoredLength);
        version.Property(x => x.ContentHash).IsRequired().IsCode(AccountRules.HashLength).IsFixedLength();
        version.Property(x => x.EditedBy).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        version.HasOne<NamingRuleChangeRequestRow>().WithMany().HasForeignKey(x => x.ActivatedByRequestId).OnDelete(DeleteBehavior.Restrict);

        var request = modelBuilder.Entity<NamingRuleChangeRequestRow>();
        request.ToTable("NamingRuleChangeRequest", t => t.HasTrigger("TR_NamingRuleChangeRequest_DecidedImmutable"));
        request.HasKey(x => x.Id);
        request.Property(x => x.ContentHash).IsRequired().IsCode(AccountRules.HashLength).IsFixedLength();
        request.Property(x => x.Maker).IsRequired().IsCode(AuditColumns.ActorMaxLength);
        request.Property(x => x.Reason).IsRequired().HasMaxLength(1000);
        request.Property(x => x.State).IsRequired().IsCode(16);
        request.Property(x => x.Checker).IsCode(AuditColumns.ActorMaxLength);
        request.Property(x => x.DecisionReason).HasMaxLength(1000);
        request.HasOne<NamingRule>().WithMany().HasForeignKey(x => x.NamingRuleId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<NamingRuleVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Restrict);
        request.HasIndex(x => new { x.State, x.ProposedAtUtc });
    }
}

/// <summary>A stored link request (DL-089a), rebuilt with <c>AccountLinkRequest.Restore</c>.</summary>
public sealed class AccountLinkRequestRow
{
    public required Guid Id { get; init; }

    public required Guid ApplicationId { get; init; }

    public required string Type { get; init; }

    public required string Name { get; init; }

    public required string NameKey { get; init; }

    public required string TargetId { get; init; }

    public required string Owner { get; init; }

    public required string Maker { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset ProposedAtUtc { get; init; }

    public required string State { get; set; }

    public string? Checker { get; set; }

    public string? DecisionReason { get; set; }

    public DateTimeOffset? DecidedAtUtc { get; set; }
}

/// <summary>A stored naming rule change request (DL-089b), rebuilt with <c>NamingRuleChangeRequest.Restore</c>.</summary>
public sealed class NamingRuleChangeRequestRow
{
    public required Guid Id { get; init; }

    public required Guid NamingRuleId { get; init; }

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
