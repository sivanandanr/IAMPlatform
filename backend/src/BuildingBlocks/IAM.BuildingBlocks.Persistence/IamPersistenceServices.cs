using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.BuildingBlocks.Persistence;

/// <summary>Configures EF Core for one provider. Implemented by IAM.Persistence.SqlServer and IAM.Persistence.PostgreSql.</summary>
public interface IIamDatabaseProvider
{
    DatabaseProvider Provider { get; }

    /// <summary>Applies the provider, migrations assembly and per-schema migrations history table.</summary>
    void Configure(DbContextOptionsBuilder options, string connectionString, string schema);

    /// <summary>
    /// True if <paramref name="exception"/> (or an inner exception) is a unique-key or primary-key violation
    /// (SQL Server 2601/2627, PostgreSQL 23505). Lets modules tell "already exists" from other failures without
    /// referencing a database driver.
    /// </summary>
    bool IsUniqueViolation(Exception exception);

    /// <summary>
    /// True if <paramref name="exception"/> (or an inner exception) means the database chose this transaction as the loser
    /// of a concurrent one (SQL Server deadlock 1205, lock timeout 1222 or snapshot update conflict 3960; PostgreSQL 40001
    /// serialization failure or 40P01 deadlock). Nothing was committed; the caller may report a conflict.
    /// </summary>
    bool IsConcurrencyFailure(Exception exception);
}

/// <summary>Supplies the connection string for a module schema. Each module connects with its own least-privilege login.</summary>
public interface IIamConnectionStrings
{
    /// <summary>Returns the connection string for <paramref name="schema"/>, or throws if none is configured.</summary>
    string ForSchema(string schema);
}

/// <summary>The server-side identity recorded in audit columns. Never taken from request payloads.</summary>
public interface IAuditActorAccessor
{
    /// <summary>The acting subject (for example the token <c>sub</c> or <c>system:&lt;worker&gt;</c>), or null if there is none.</summary>
    string? CurrentActor { get; }
}

/// <summary>Records which module schemas have a DbContext, so the host can verify their configuration at startup.</summary>
public sealed class IamPersistenceRegistry
{
    private readonly HashSet<string> _schemas = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Schemas => _schemas;

    internal void Add(string schema) => _schemas.Add(schema);
}

public static class IamPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a module DbContext. The provider, connection string and actor come from the host; the module never
    /// sees a database driver. Sensitive data logging is always off here (ADR-0017).
    /// </summary>
    public static IServiceCollection AddIamDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : IamDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        IamDbContext.ValidateSchemaName(schema);

        var registry = services
            .Where(d => d.ServiceType == typeof(IamPersistenceRegistry))
            .Select(d => d.ImplementationInstance)
            .OfType<IamPersistenceRegistry>()
            .FirstOrDefault();
        if (registry is null)
        {
            registry = new IamPersistenceRegistry();
            services.AddSingleton(registry);
        }

        registry.Add(schema);
        services.AddDbContext<TContext>((sp, options) =>
        {
            var provider = sp.GetRequiredService<IIamDatabaseProvider>();
            var connectionString = sp.GetRequiredService<IIamConnectionStrings>().ForSchema(schema);
            provider.Configure(options, connectionString, schema);
            options.AddInterceptors(new AuditingSaveChangesInterceptor(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IAuditActorAccessor>()));
            options.EnableSensitiveDataLogging(false);
        });
        return services;
    }

    /// <summary>
    /// Common options every provider applies: the model cache is keyed by provider as well as context type, so one
    /// process can never reuse a SQL Server model for PostgreSQL or the reverse.
    /// </summary>
    public static DbContextOptionsBuilder UseIamDefaults(this DbContextOptionsBuilder options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>();
    }
}

internal sealed class ProviderAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), context.Database.ProviderName, designTime);
}
