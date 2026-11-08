using IAM.Modules.Identity.Contracts;
using IAM.Modules.Identity.Domain.Attributes;
using IAM.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Identity.Infrastructure.Attributes;

/// <summary>
/// <see cref="IAttributeCatalog"/> for other modules: published versions only, read without tracking. If more than one
/// version of an attribute were published, the highest version number wins.
/// </summary>
public sealed class AttributeCatalogReader(IdentityDbContext db) : IAttributeCatalog
{
    public async Task<PublishedAttributeDefinition?> FindPublishedByKeyAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            AttributeRules.ValidateKey(key);
        }
        catch (AttributeDefinitionRejectedException)
        {
            return null;
        }

        var definition = await db.AttributeDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Key == key, cancellationToken)
            .ConfigureAwait(false);
        return definition is null ? null : await PublishedAsync(definition.Id, definition.Key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PublishedAttributeDefinition?> FindPublishedByIdAsync(Guid attributeDefinitionId, CancellationToken cancellationToken)
    {
        var definition = await db.AttributeDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == attributeDefinitionId, cancellationToken)
            .ConfigureAwait(false);
        return definition is null ? null : await PublishedAsync(definition.Id, definition.Key, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PublishedAttributeDefinition?> PublishedAsync(Guid definitionId, string key, CancellationToken cancellationToken)
    {
        var version = await db.AttributeDefinitionVersions.AsNoTracking()
            .Include(v => v.Operators)
            .Where(v => v.AttributeDefinitionId == definitionId && v.State == AttributeVersionState.Published)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return null;
        }

        return new PublishedAttributeDefinition(
            definitionId,
            version.Id,
            version.VersionNumber,
            key,
            version.DisplayName,
            version.DataType.ToString(),
            version.SubjectScope.ToString(),
            version.SourceSystem,
            version.Sensitivity.ToString(),
            version.Normalization.ToString(),
            version.FreshnessMaxAgeMinutes,
            version.MaxLength,
            version.OperatorList.Select(o => o.ToString()).ToList(),
            version.ContentHash);
    }
}
