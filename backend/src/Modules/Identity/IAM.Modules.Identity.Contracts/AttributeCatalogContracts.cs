namespace IAM.Modules.Identity.Contracts;

/// <summary>
/// Read-only view of the shared attribute catalog for other modules (Policy, Workflow) (ADR-0015, IAM-WF-013/014).
/// Only <b>published</b> versions are returned: drafts, unknown and retired attributes are never visible to rules. In
/// S1-08 nothing can be published yet (DL-007), so every lookup returns null.
/// </summary>
public interface IAttributeCatalog
{
    Task<PublishedAttributeDefinition?> FindPublishedByKeyAsync(string key, CancellationToken cancellationToken);

    Task<PublishedAttributeDefinition?> FindPublishedByIdAsync(Guid attributeDefinitionId, CancellationToken cancellationToken);
}

/// <summary>
/// A published attribute version. Enum-like fields carry their stable names (for example <c>Code</c>, <c>In</c>), so
/// this contract does not depend on Identity's domain types. Rules reference <see cref="VersionId"/> and
/// <see cref="ContentHash"/> as evidence of exactly which definition they used.
/// </summary>
public sealed record PublishedAttributeDefinition(
    Guid AttributeDefinitionId,
    Guid VersionId,
    int VersionNumber,
    string Key,
    string DisplayName,
    string DataType,
    string SubjectScope,
    string SourceSystem,
    string Sensitivity,
    string Normalization,
    int FreshnessMaxAgeMinutes,
    int? MaxLength,
    IReadOnlyList<string> Operators,
    string ContentHash);
