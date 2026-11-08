using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace IAM.BuildingBlocks.Persistence;

/// <summary>
/// Base for every module DbContext: one schema per module, IAM conventions applied after the module's own model
/// configuration (ADR-0017).
/// </summary>
public abstract partial class IamDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>The module's schema, lower-case (for example <c>identity</c>).</summary>
    public abstract string Schema { get; }

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ValidateSchemaName(Schema);
        modelBuilder.HasDefaultSchema(Schema);
        ConfigureModel(modelBuilder);
        modelBuilder.ApplyIamConventions(DatabaseProviderSetting.FromEfProviderName(Database.ProviderName));
    }

    protected sealed override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.ApplyIamPreConventions();
    }

    /// <summary>Module entity configuration. Tables must stay in <see cref="Schema"/>.</summary>
    protected abstract void ConfigureModel(ModelBuilder modelBuilder);

    public static void ValidateSchemaName(string schema)
    {
        if (schema is null || !SchemaPattern().IsMatch(schema))
        {
            throw new ArgumentException("Schema names must be lower-case letters, digits or '_' and start with a letter (ADR-0017).", nameof(schema));
        }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SchemaPattern();
}
