using IAM.Modules.Identity.Domain.Attributes;

namespace IAM.Modules.Identity.Application.Attributes;

/// <summary>
/// Narrow request for an attribute draft (BACKEND_INPUT_VALIDATION: explicit DTO, enum allowlists by exact name, no
/// server-owned fields such as Id, State, ContentHash, CreatedBy or Version).
/// </summary>
public sealed record AttributeDraftRequest(
    string? DisplayName,
    string? DataType,
    string? SubjectScope,
    string? SourceSystem,
    string? Sensitivity,
    string? Normalization,
    int? FreshnessMaxAgeMinutes,
    int? MaxLength,
    IReadOnlyList<string?>? Operators);

public sealed record RegisterAttributeRequest(string? Key, AttributeDraftRequest? Draft);

/// <summary>What an administrator sees of an attribute and its current draft.</summary>
public sealed record AttributeDraftView(
    Guid DefinitionId,
    string Key,
    Guid VersionId,
    int VersionNumber,
    string State,
    AttributeDraft Content,
    string ContentHash,
    long ConcurrencyVersion);

/// <summary>The draft was changed by someone else since it was read; reload and try again.</summary>
public sealed class AttributeDraftConflictException : Exception
{
    public AttributeDraftConflictException()
        : base("The attribute draft was changed by someone else; reload it and try again.")
    {
    }

    public AttributeDraftConflictException(string message)
        : base(message)
    {
    }

    public AttributeDraftConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AttributeDefinitionNotFoundException : Exception
{
    public AttributeDefinitionNotFoundException()
        : base("The attribute definition does not exist.")
    {
    }

    public AttributeDefinitionNotFoundException(string message)
        : base(message)
    {
    }

    public AttributeDefinitionNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Administration of the attribute catalog (S1-08). Every change is written together with its audit event in one
/// transaction (ADR-0018). There is no HTTP endpoint yet: catalog administration waits for the permission model
/// (R-023, ADR-0008, P0-013).
/// </summary>
public interface IAttributeCatalogAdministration
{
    Task<AttributeDraftView> RegisterAsync(RegisterAttributeRequest request, CancellationToken cancellationToken);

    /// <summary>Replaces the current draft. <paramref name="expectedConcurrencyVersion"/> must match the draft's current version.</summary>
    Task<AttributeDraftView> UpdateDraftAsync(Guid definitionId, long expectedConcurrencyVersion, AttributeDraftRequest request, CancellationToken cancellationToken);

    /// <summary>Always refused in S1-08 (DL-007); the refusal itself is audited. Throws <see cref="AttributeActivationLockedException"/>.</summary>
    Task RequestPublishAsync(Guid definitionId, CancellationToken cancellationToken);

    Task<AttributeDraftView?> FindDraftAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Maps requests to domain drafts. Enum values must be exact defined names; numbers and other spellings are refused.</summary>
public static class AttributeRequestMapper
{
    public static AttributeDraft ToDraft(AttributeDraftRequest? request)
    {
        if (request is null)
        {
            throw new AttributeDefinitionRejectedException("Draft", "is required");
        }

        if (request.Operators is null)
        {
            throw new AttributeDefinitionRejectedException("Operators", "are required");
        }

        if (request.Operators.Count > Enum.GetValues<AttributeOperator>().Length)
        {
            throw new AttributeDefinitionRejectedException("Operators", "has too many entries");
        }

        return new AttributeDraft(
            request.DisplayName ?? throw new AttributeDefinitionRejectedException("DisplayName", "is required"),
            Parse<AttributeDataType>(request.DataType, "DataType"),
            Parse<AttributeSubjectScope>(request.SubjectScope, "SubjectScope"),
            request.SourceSystem ?? throw new AttributeDefinitionRejectedException("SourceSystem", "is required"),
            Parse<AttributeSensitivity>(request.Sensitivity, "Sensitivity"),
            request.Normalization is null ? AttributeNormalization.None : Parse<AttributeNormalization>(request.Normalization, "Normalization"),
            request.FreshnessMaxAgeMinutes ?? throw new AttributeDefinitionRejectedException("FreshnessMaxAgeMinutes", "is required"),
            request.MaxLength,
            request.Operators.Select(o => Parse<AttributeOperator>(o, "Operators")).ToList());
    }

    /// <summary>Exact, case-sensitive name of a defined value. "1", "code" or "Code,Text" are refused.</summary>
    public static TEnum Parse<TEnum>(string? value, string field)
        where TEnum : struct, Enum
    {
        if (value is not null && Enum.GetNames<TEnum>().Contains(value, StringComparer.Ordinal))
        {
            return Enum.Parse<TEnum>(value, ignoreCase: false);
        }

        throw new AttributeDefinitionRejectedException(field, $"must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}");
    }
}
