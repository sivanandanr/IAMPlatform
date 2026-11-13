using IAM.Modules.Administration.Infrastructure.Persistence;
using IAM.Modules.Audit.Infrastructure.Persistence;
using IAM.Modules.Identity.Infrastructure.Persistence;
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
