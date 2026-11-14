using IAM.Modules.Administration.Infrastructure.Persistence;
using IAM.Modules.Audit.Infrastructure.Persistence;
using IAM.Modules.Catalog.Infrastructure.Persistence;
using IAM.Modules.Identity.Infrastructure.Persistence;
using IAM.Modules.Organization.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IAM.Persistence.PostgreSql;

/// <summary>
/// Used only by <c>dotnet ef migrations add</c>. The placeholder connection string is never opened; migrations are
/// applied by the migrator identity at deployment time, not from here.
/// </summary>
internal sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    internal const string PlaceholderConnectionString = "Host=design-time-only;Database=iam";

    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        new PostgreSqlDatabaseProvider().Configure(options, PlaceholderConnectionString, IdentityDbContext.SchemaName);
        return new IdentityDbContext(options.Options);
    }
}

internal sealed class AuditDesignTimeFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>();
        new PostgreSqlDatabaseProvider().Configure(options, IdentityDesignTimeFactory.PlaceholderConnectionString, AuditDbContext.SchemaName);
        return new AuditDbContext(options.Options);
    }
}

internal sealed class AdministrationDesignTimeFactory : IDesignTimeDbContextFactory<AdministrationDbContext>
{
    public AdministrationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AdministrationDbContext>();
        new PostgreSqlDatabaseProvider().Configure(options, IdentityDesignTimeFactory.PlaceholderConnectionString, AdministrationDbContext.SchemaName);
        return new AdministrationDbContext(options.Options);
    }
}

internal sealed class CatalogDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>();
        new PostgreSqlDatabaseProvider().Configure(options, IdentityDesignTimeFactory.PlaceholderConnectionString, CatalogDbContext.SchemaName);
        return new CatalogDbContext(options.Options);
    }
}

internal sealed class OrganizationDesignTimeFactory : IDesignTimeDbContextFactory<OrganizationDbContext>
{
    public OrganizationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrganizationDbContext>();
        new PostgreSqlDatabaseProvider().Configure(options, IdentityDesignTimeFactory.PlaceholderConnectionString, OrganizationDbContext.SchemaName);
        return new OrganizationDbContext(options.Options);
    }
}
