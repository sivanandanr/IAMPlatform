using IAM.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Identity module DbContext, schema <c>identity</c> (ADR-0017). S1-06 establishes the schema and migration history
/// only; the attribute catalog tables arrive with S1-08.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : IamDbContext(options)
{
    public const string SchemaName = "identity";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        // No tables yet (S1-08 adds the attribute catalog).
    }
}
