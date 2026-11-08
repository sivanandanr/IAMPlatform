using IAM.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Persistence.SqlServer;

/// <summary>SQL Server / Azure SQL MI provider. No automatic retry: only idempotent units may retry (ADR-0017).</summary>
public sealed class SqlServerDatabaseProvider : IIamDatabaseProvider
{
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DatabaseProvider Provider => DatabaseProvider.SqlServer;

    public void Configure(DbContextOptionsBuilder options, string connectionString, string schema)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        IamDbContext.ValidateSchemaName(schema);

        options.UseSqlServer(connectionString, sql =>
        {
            sql.MigrationsAssembly(typeof(SqlServerDatabaseProvider).Assembly.GetName().Name);
            sql.MigrationsHistoryTable(MigrationsHistoryTable, schema);
            sql.CommandTimeout(30);
        });
        options.UseIamDefaults();
    }

    public bool IsUniqueViolation(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
            {
                return true;
            }
        }

        return false;
    }
}
