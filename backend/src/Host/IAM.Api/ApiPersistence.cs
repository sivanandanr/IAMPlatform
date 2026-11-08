using System.Security.Claims;
using IAM.BuildingBlocks.Persistence;
using IAM.Persistence.PostgreSql;
using IAM.Persistence.SqlServer;

namespace IAM.Api;

/// <summary>
/// Persistence composition (S1-06, ADR-0002, ADR-0017): exactly one provider chosen by <c>IAM_DB_PROVIDER</c>, one
/// connection string per module schema (<c>ConnectionStrings:&lt;schema&gt;</c>, each with its own least-privilege
/// login), and the audit actor taken from the validated token only. Anything missing stops startup.
/// </summary>
internal static class ApiPersistence
{
    public static IServiceCollection AddIamPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = DatabaseProviderSetting.Parse(configuration[DatabaseProviderSetting.Key]);
        services.AddSingleton<IIamDatabaseProvider>(provider switch
        {
            DatabaseProvider.SqlServer => new SqlServerDatabaseProvider(),
            DatabaseProvider.PostgreSql => new PostgreSqlDatabaseProvider(),
            _ => throw new InvalidOperationException("Unsupported database provider."),
        });
        services.AddSingleton<IIamConnectionStrings>(new ConfigurationConnectionStrings(configuration));
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddScoped<IAuditActorAccessor, HttpContextAuditActorAccessor>();
        return services;
    }

    /// <summary>Fails startup if any registered module schema has no connection string.</summary>
    public static void EnsurePersistenceConfigured(IServiceProvider services)
    {
        var registry = services.GetService<IamPersistenceRegistry>();
        var connectionStrings = services.GetRequiredService<IIamConnectionStrings>();
        foreach (var schema in registry?.Schemas ?? [])
        {
            connectionStrings.ForSchema(schema);
        }
    }

    private sealed class ConfigurationConnectionStrings(IConfiguration configuration) : IIamConnectionStrings
    {
        public string ForSchema(string schema)
        {
            var value = configuration.GetConnectionString(schema);
            return string.IsNullOrWhiteSpace(value)
                ? throw new InvalidOperationException(
                    $"ConnectionStrings:{schema} is not configured. Each module schema needs its own least-privilege login (ADR-0017).")
                : value;
        }
    }

    /// <summary>The validated token's <c>sub</c>; null when there is no authenticated user, so saving fails closed.</summary>
    private sealed class HttpContextAuditActorAccessor(IHttpContextAccessor accessor) : IAuditActorAccessor
    {
        public string? CurrentActor
        {
            get
            {
                var user = accessor.HttpContext?.User;
                return user?.Identity?.IsAuthenticated == true ? user.FindFirstValue("sub") : null;
            }
        }
    }
}
