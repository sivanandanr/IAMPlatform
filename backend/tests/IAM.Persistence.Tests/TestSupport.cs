using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Account.Infrastructure.Persistence;
using IAM.Modules.Administration.Infrastructure.Persistence;
using IAM.Modules.Audit.Infrastructure.Persistence;
using IAM.Modules.Catalog.Infrastructure.Persistence;
using IAM.Modules.Identity.Infrastructure.Persistence;
using IAM.Modules.Notification.Infrastructure.Persistence;
using IAM.Modules.Organization.Infrastructure.Persistence;
using IAM.Modules.Policy.Infrastructure.Persistence;
using IAM.Modules.Provisioning.Infrastructure.Persistence;
using IAM.Modules.Request.Infrastructure.Persistence;
using IAM.Modules.Workflow.Infrastructure.Persistence;
using IAM.Persistence.PostgreSql;
using IAM.Persistence.SqlServer;
using IAM.Persistence.Tests.Probe;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IAM.Persistence.Tests;

internal static class TestSupport
{
    public static IIamDatabaseProvider ProviderFor(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => new SqlServerDatabaseProvider(),
        DatabaseProvider.PostgreSql => new PostgreSqlDatabaseProvider(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>A connection string that is never opened: model-only tests.</summary>
    public static string OfflineConnectionString(DatabaseProvider provider) => provider == DatabaseProvider.SqlServer
        ? "Server=offline.invalid;Database=iam;Encrypt=true"
        : "Host=offline.invalid;Database=iam";

    public static ProbeDbContext CreateProbe(DatabaseProvider provider, string connectionString, AuditingSaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<ProbeDbContext>();
        ProviderFor(provider).Configure(options, connectionString, ProbeDbContext.SchemaName);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new ProbeDbContext(options.Options);
    }

    public static IdentityDbContext CreateIdentity(DatabaseProvider provider, string connectionString, AuditingSaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        ProviderFor(provider).Configure(options, connectionString, IdentityDbContext.SchemaName);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new IdentityDbContext(options.Options);
    }

    public static AuditDbContext CreateAudit(DatabaseProvider provider, string connectionString, AuditingSaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>();
        ProviderFor(provider).Configure(options, connectionString, AuditDbContext.SchemaName);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new AuditDbContext(options.Options);
    }

    public static AdministrationDbContext CreateAdministration(DatabaseProvider provider, string connectionString, AuditingSaveChangesInterceptor? interceptor = null, IInterceptor? extra = null)
    {
        var options = new DbContextOptionsBuilder<AdministrationDbContext>();
        ProviderFor(provider).Configure(options, connectionString, AdministrationDbContext.SchemaName);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        if (extra is not null)
        {
            options.AddInterceptors(extra);
        }

        return new AdministrationDbContext(options.Options);
    }

    public static CatalogDbContext CreateCatalog(DatabaseProvider provider, string connectionString, AuditingSaveChangesInterceptor? interceptor = null, IInterceptor? extra = null)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>();
        ProviderFor(provider).Configure(options, connectionString, CatalogDbContext.SchemaName);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        if (extra is not null)
        {
            options.AddInterceptors(extra);
        }

        return new CatalogDbContext(options.Options);
    }

    public static AccountDbContext CreateAccount(DatabaseProvider provider, string connectionString, AuditingSaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AccountDbContext>();
        ProviderFor(provider).Configure(options, connectionString, AccountDbContext.SchemaName);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new AccountDbContext(options.Options);
    }

    public static PolicyDbContext CreatePolicy(DatabaseProvider provider, string connectionString, AuditingSaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PolicyDbContext>();
        ProviderFor(provider).Configure(options, connectionString, PolicyDbContext.SchemaName);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new PolicyDbContext(options.Options);
    }

    public static ProvisioningDbContext CreateProvisioning(DatabaseProvider provider, string connectionString, AuditingSaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<ProvisioningDbContext>();
        ProviderFor(provider).Configure(options, connectionString, ProvisioningDbContext.SchemaName);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new ProvisioningDbContext(options.Options);
    }

    public static RequestDbContext CreateRequest(DatabaseProvider provider, string connectionString, AuditingSaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<RequestDbContext>();
        ProviderFor(provider).Configure(options, connectionString, RequestDbContext.SchemaName);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new RequestDbContext(options.Options);
    }

    public static OrganizationDbContext CreateOrganization(DatabaseProvider provider, string connectionString, AuditingSaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<OrganizationDbContext>();
        ProviderFor(provider).Configure(options, connectionString, OrganizationDbContext.SchemaName);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new OrganizationDbContext(options.Options);
    }

    public static NotificationDbContext CreateNotification(DatabaseProvider provider, string connectionString, AuditingSaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<NotificationDbContext>();
        ProviderFor(provider).Configure(options, connectionString, NotificationDbContext.SchemaName);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new NotificationDbContext(options.Options);
    }

    public static WorkflowDbContext CreateWorkflow(DatabaseProvider provider, string connectionString, AuditingSaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>();
        ProviderFor(provider).Configure(options, connectionString, WorkflowDbContext.SchemaName);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new WorkflowDbContext(options.Options);
    }

    public static IEnumerable<Exception> Flatten(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }
}

/// <summary>No request items: for workflow tests that don't involve one, so a completion records no account name (DL-099).</summary>
internal sealed class NoItemSource : IAM.Modules.Request.Contracts.IRequestItemSource
{
    public Task<IAM.Modules.Request.Contracts.ProvisionableItem?> FindAsync(Guid itemId, IAM.Modules.Request.Contracts.ApprovedAccountName? approvedName, CancellationToken cancellationToken) =>
        Task.FromResult<IAM.Modules.Request.Contracts.ProvisionableItem?>(null);

    public Task<IAM.Modules.Request.Contracts.AccountNameSnapshot?> CurrentAccountNameAsync(Guid itemId, CancellationToken cancellationToken) =>
        Task.FromResult<IAM.Modules.Request.Contracts.AccountNameSnapshot?>(null);

    public Task<IAM.Modules.Request.Contracts.RequestItemSubmitted?> FindSubmittedAsync(Guid itemId, CancellationToken cancellationToken) =>
        Task.FromResult<IAM.Modules.Request.Contracts.RequestItemSubmitted?>(null);
}

internal sealed class FixedActor(string? actor) : IAuditActorAccessor
{
    public string? CurrentActor { get; } = actor;
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
