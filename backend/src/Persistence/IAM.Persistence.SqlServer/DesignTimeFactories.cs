using IAM.Modules.Account.Infrastructure.Persistence;
using IAM.Modules.Administration.Infrastructure.Persistence;
using IAM.Modules.Audit.Infrastructure.Persistence;
using IAM.Modules.Catalog.Infrastructure.Persistence;
using IAM.Modules.Identity.Infrastructure.Persistence;
using IAM.Modules.Notification.Infrastructure.Persistence;
using IAM.Modules.Organization.Infrastructure.Persistence;
using IAM.Modules.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IAM.Persistence.SqlServer;

/// <summary>
/// Used only by <c>dotnet ef migrations add</c>. The placeholder connection string is never opened; migrations are
/// applied by the migrator identity at deployment time, not from here.
/// </summary>
internal sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    internal const string PlaceholderConnectionString = "Server=design-time-only;Database=iam;Encrypt=true";

    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        new SqlServerDatabaseProvider().Configure(options, PlaceholderConnectionString, IdentityDbContext.SchemaName);
        return new IdentityDbContext(options.Options);
    }
}

internal sealed class AuditDesignTimeFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>();
        new SqlServerDatabaseProvider().Configure(options, IdentityDesignTimeFactory.PlaceholderConnectionString, AuditDbContext.SchemaName);
        return new AuditDbContext(options.Options);
    }
}

internal sealed class AdministrationDesignTimeFactory : IDesignTimeDbContextFactory<AdministrationDbContext>
{
    public AdministrationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AdministrationDbContext>();
        new SqlServerDatabaseProvider().Configure(options, IdentityDesignTimeFactory.PlaceholderConnectionString, AdministrationDbContext.SchemaName);
        return new AdministrationDbContext(options.Options);
    }
}

internal sealed class CatalogDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>();
        new SqlServerDatabaseProvider().Configure(options, IdentityDesignTimeFactory.PlaceholderConnectionString, CatalogDbContext.SchemaName);
        return new CatalogDbContext(options.Options);
    }
}

internal sealed class OrganizationDesignTimeFactory : IDesignTimeDbContextFactory<OrganizationDbContext>
{
    public OrganizationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrganizationDbContext>();
        new SqlServerDatabaseProvider().Configure(options, IdentityDesignTimeFactory.PlaceholderConnectionString, OrganizationDbContext.SchemaName);
        return new OrganizationDbContext(options.Options);
    }
}

internal sealed class WorkflowDesignTimeFactory : IDesignTimeDbContextFactory<WorkflowDbContext>
{
    public WorkflowDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>();
        new SqlServerDatabaseProvider().Configure(options, IdentityDesignTimeFactory.PlaceholderConnectionString, WorkflowDbContext.SchemaName);
        return new WorkflowDbContext(options.Options);
    }
}

internal sealed class NotificationDesignTimeFactory : IDesignTimeDbContextFactory<NotificationDbContext>
{
    public NotificationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationDbContext>();
        new SqlServerDatabaseProvider().Configure(options, IdentityDesignTimeFactory.PlaceholderConnectionString, NotificationDbContext.SchemaName);
        return new NotificationDbContext(options.Options);
    }
}

internal sealed class AccountDesignTimeFactory : IDesignTimeDbContextFactory<AccountDbContext>
{
    public AccountDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AccountDbContext>();
        new SqlServerDatabaseProvider().Configure(options, IdentityDesignTimeFactory.PlaceholderConnectionString, AccountDbContext.SchemaName);
        return new AccountDbContext(options.Options);
    }
}
