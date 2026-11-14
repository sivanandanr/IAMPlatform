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
/// Administration of the attribute catalog (S1-08, DL-007, DL-064). Every change is written together with its audit
/// event in one transaction (ADR-0018). Publishing needs two people: one proposes publishing an exact draft, a different
/// person with the approve permission decides; refusals are audited. Who acts always comes from the server-side actor.
/// </summary>
public interface IAttributeCatalogAdministration
{
    Task<AttributeDraftView> RegisterAsync(RegisterAttributeRequest request, CancellationToken cancellationToken);

    /// <summary>Replaces the current draft. <paramref name="expectedConcurrencyVersion"/> must match the draft's current version.</summary>
    Task<AttributeDraftView> UpdateDraftAsync(Guid definitionId, long expectedConcurrencyVersion, AttributeDraftRequest request, CancellationToken cancellationToken);

    /// <summary>Starts the next version as a draft, once a version is published and no draft exists.</summary>
    Task<AttributeDraftView> StartNewDraftAsync(Guid definitionId, AttributeDraftRequest request, CancellationToken cancellationToken);

    Task<AttributeDraftView?> FindDraftAsync(string key, CancellationToken cancellationToken);

    /// <summary>Proposes publishing the current draft exactly as it is. Needs <c>identity.attributes.manage</c>.</summary>
    Task<AttributePublishRequestView> ProposePublishAsync(Guid definitionId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes the named draft and retires the previous version. Needs <c>identity.attributes.approve</c>; never the maker,
    /// nor the draft's creator or last editor. <paramref name="reviewedContentHash"/> is the hash of the content the approver
    /// reviewed: it must be the request's and the current draft's (DL-065).
    /// </summary>
    Task<AttributePublishRequestView> ApprovePublishAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken);

    Task<AttributePublishRequestView> RejectPublishAsync(Guid requestId, string reason, CancellationToken cancellationToken);

    /// <summary>Only the maker can withdraw their own request.</summary>
    Task<AttributePublishRequestView> WithdrawPublishAsync(Guid requestId, string? reason, CancellationToken cancellationToken);

    Task<AttributePublishRequestView?> FindPublishRequestAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>A request with the draft it would publish, for an approver to review; the draft is absent once it is no longer a draft.</summary>
    Task<AttributePublishReview?> ReviewPublishRequestAsync(Guid requestId, CancellationToken cancellationToken);
}

/// <summary>A publish request as callers see it: plain values only.</summary>
public sealed record AttributePublishRequestView(
    Guid Id,
    Guid DefinitionId,
    int VersionNumber,
    string ContentHash,
    string Maker,
    string Reason,
    DateTimeOffset ProposedAtUtc,
    string State,
    string? Checker,
    string? DecisionReason,
    DateTimeOffset? DecidedAtUtc);

/// <summary>A publish request and the current draft it names. The approver checks the content and sends its hash back.</summary>
public sealed record AttributePublishReview(AttributePublishRequestView Request, AttributeDraftView? Draft);

/// <summary>Someone else decided or changed the same request or draft first. Nothing was saved.</summary>
public sealed class AttributePublishConflictException : Exception
{
    public AttributePublishConflictException()
        : base("The attribute or its publish request was changed by someone else; reload it and try again.")
    {
    }

    public AttributePublishConflictException(string message)
        : base(message)
    {
    }

    public AttributePublishConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AttributePublishRequestNotFoundException : Exception
{
    public AttributePublishRequestNotFoundException()
        : base("No such attribute publish request.")
    {
    }

    public AttributePublishRequestNotFoundException(string message)
        : base(message)
    {
    }

    public AttributePublishRequestNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
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
