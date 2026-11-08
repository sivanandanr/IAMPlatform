using IAM.Modules.Identity.Infrastructure.Persistence;
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
