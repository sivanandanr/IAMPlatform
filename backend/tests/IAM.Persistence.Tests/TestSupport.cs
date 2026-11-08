using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Infrastructure.Persistence;
using IAM.Modules.Identity.Infrastructure.Persistence;
using IAM.Persistence.PostgreSql;
using IAM.Persistence.SqlServer;
using IAM.Persistence.Tests.Probe;
using Microsoft.EntityFrameworkCore;

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

    public static IEnumerable<Exception> Flatten(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }
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
