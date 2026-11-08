namespace IAM.BuildingBlocks.Persistence;

/// <summary>The single relational engine a deployment runs on (ADR-0002). There is deliberately no default.</summary>
public enum DatabaseProvider
{
    SqlServer = 1,
    PostgreSql = 2,
}

/// <summary>Reads the <c>IAM_DB_PROVIDER</c> setting. Missing or unknown values fail closed; nothing falls back.</summary>
public static class DatabaseProviderSetting
{
    /// <summary>Configuration key (environment variable) that selects the provider.</summary>
    public const string Key = "IAM_DB_PROVIDER";

    public static DatabaseProvider Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{Key} is not set. Set it to 'SqlServer' or 'PostgreSql'; there is no default (ADR-0002).");
        }

        // Exact, case-sensitive names only: no numeric values, no partial matches.
        return value switch
        {
            "SqlServer" => DatabaseProvider.SqlServer,
            "PostgreSql" => DatabaseProvider.PostgreSql,
            _ => throw new InvalidOperationException(
                $"{Key} has an unsupported value. Use 'SqlServer' or 'PostgreSql' (ADR-0002)."),
        };
    }

    /// <summary>Maps the EF Core provider name of a live context back to <see cref="DatabaseProvider"/>.</summary>
    public static DatabaseProvider FromEfProviderName(string? providerName) => providerName switch
    {
        "Microsoft.EntityFrameworkCore.SqlServer" => DatabaseProvider.SqlServer,
        "Npgsql.EntityFrameworkCore.PostgreSQL" => DatabaseProvider.PostgreSql,
        _ => throw new InvalidOperationException(
            $"EF Core provider '{providerName}' is not supported. Only SQL Server and PostgreSQL are allowed (ADR-0002, ADR-0010)."),
    };
}
