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
}
