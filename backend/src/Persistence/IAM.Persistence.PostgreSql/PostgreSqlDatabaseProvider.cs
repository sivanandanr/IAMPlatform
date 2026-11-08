using IAM.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Persistence.PostgreSql;

/// <summary>PostgreSQL provider. No automatic retry: only idempotent units may retry (ADR-0017).</summary>
public sealed class PostgreSqlDatabaseProvider : IIamDatabaseProvider
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DatabaseProvider Provider => DatabaseProvider.PostgreSql;

    public void Configure(DbContextOptionsBuilder options, string connectionString, string schema)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        IamDbContext.ValidateSchemaName(schema);

        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsAssembly(typeof(PostgreSqlDatabaseProvider).Assembly.GetName().Name);
            npgsql.MigrationsHistoryTable(MigrationsHistoryTable, schema);
            npgsql.CommandTimeout(30);
        });
        options.UseIamDefaults();
    }

    public bool IsUniqueViolation(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }
}
