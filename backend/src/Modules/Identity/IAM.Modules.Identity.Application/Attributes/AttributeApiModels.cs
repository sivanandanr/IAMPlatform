using IAM.Modules.Identity.Domain.Attributes;

namespace IAM.Modules.Identity.Application.Attributes;

/// <summary>An attribute version as the API returns it: plain values, enum names as text.</summary>
public sealed record AttributeVersionResponse(
    Guid DefinitionId,
    string Key,
    Guid VersionId,
    int VersionNumber,
    string State,
    string DisplayName,
    string DataType,
    string SubjectScope,
    string SourceSystem,
    string Sensitivity,
    string Normalization,
    int FreshnessMaxAgeMinutes,
    int? MaxLength,
    IReadOnlyList<string> Operators,
    string ContentHash,
    long ConcurrencyVersion);

/// <summary>How a caller-facing layer should treat a catalog failure, without naming domain types.</summary>
public enum AttributeFailureKind
{
    None = 0,

    /// <summary>A field breaks a rule; <c>field</c> names it and the message describes the rule, never the value.</summary>
    InvalidField = 1,

    /// <summary>Who the caller is or what they hold rules it out (not allowed, own request, no longer allowed).</summary>
    Forbidden = 2,

    /// <summary>The attribute or request is not in a state that allows this.</summary>
    WrongState = 3,

    /// <summary>The request itself is invalid (for example a missing reason).</summary>
    Invalid = 4,

    /// <summary>Someone else changed the same thing first.</summary>
    Conflict = 5,

    NotFound = 6,

    /// <summary>A stored record failed its checks: fail closed.</summary>
    Unavailable = 7,
}

public static class AttributeApi
{
    public static AttributeVersionResponse ToResponse(AttributeDraftView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var c = view.Content;
        return new AttributeVersionResponse(
            view.DefinitionId, view.Key, view.VersionId, view.VersionNumber, view.State, c.DisplayName, c.DataType.ToString(),
            c.SubjectScope.ToString(), c.SourceSystem, c.Sensitivity.ToString(), c.Normalization.ToString(), c.FreshnessMaxAgeMinutes,
            c.MaxLength, [.. c.Operators.Select(o => o.ToString())], view.ContentHash, view.ConcurrencyVersion);
    }

    /// <summary>Classifies a catalog failure. <paramref name="code"/> is an enum name and <paramref name="field"/> a field name, never data.</summary>
    public static AttributeFailureKind Classify(Exception exception, out string? code, out string? field, out string? rule)
    {
        code = null;
        field = null;
        rule = null;
        switch (exception)
        {
            case AttributeDefinitionRejectedException rejected:
                field = rejected.Field;
                rule = rejected.Message;
                return AttributeFailureKind.InvalidField;
            case AttributeDraftConflictException or AttributePublishConflictException:
                return AttributeFailureKind.Conflict;
            case AttributeDefinitionNotFoundException or AttributePublishRequestNotFoundException:
                return AttributeFailureKind.NotFound;
            case AttributePublishRefusedException refused:
                code = refused.Refusal?.ToString();
                return refused.Refusal switch
                {
                    AttributePublishRefusal.NotAllowed or AttributePublishRefusal.MakerCannotDecide or AttributePublishRefusal.NotMaker
                        or AttributePublishRefusal.MakerNoLongerAllowed => AttributeFailureKind.Forbidden,
                    AttributePublishRefusal.NotProposed or AttributePublishRefusal.AlreadyPending or AttributePublishRefusal.DraftExists
                        or AttributePublishRefusal.NothingPublished or AttributePublishRefusal.NoDraft
                        or AttributePublishRefusal.DraftChanged => AttributeFailureKind.WrongState,
                    AttributePublishRefusal.InvalidRecord => AttributeFailureKind.Unavailable,
                    _ => AttributeFailureKind.Invalid,
                };
            default:
                return AttributeFailureKind.None;
        }
    }
}
