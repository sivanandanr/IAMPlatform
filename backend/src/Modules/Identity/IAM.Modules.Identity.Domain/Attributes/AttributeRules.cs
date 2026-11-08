using System.Text.RegularExpressions;

namespace IAM.Modules.Identity.Domain.Attributes;

/// <summary>The attribute definition was refused. The message names the field and rule only.</summary>
public sealed class AttributeDefinitionRejectedException : Exception
{
    public AttributeDefinitionRejectedException()
    {
    }

    public AttributeDefinitionRejectedException(string message)
        : base(message)
    {
    }

    public AttributeDefinitionRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AttributeDefinitionRejectedException(string field, string rule, Exception? innerException = null)
        : base($"Attribute definition rejected: {field} {rule}.", innerException)
    {
        Field = field;
    }

    public string? Field { get; }
}

/// <summary>
/// Publishing or activating an attribute is refused until the shared maker-checker workflow exists (DL-007,
/// IAM-WF-019): no definition can become visible to rules on one person's say-so.
/// </summary>
public sealed class AttributeActivationLockedException : Exception
{
    public const string Reason = "Attribute activation is locked until maker-checker approval is built (DL-007).";

    public AttributeActivationLockedException()
        : base(Reason)
    {
    }

    public AttributeActivationLockedException(string message)
        : base(message)
    {
    }

    public AttributeActivationLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Static rules of the attribute catalog: key format, field bounds and the operators each type allows.</summary>
public static partial class AttributeRules
{
    public const int KeyMaxLength = 64;
    public const int DisplayNameMaxLength = 200;
    public const int SourceSystemMaxLength = 64;
    public const int MaxValueLength = 400;
    public const int MinFreshnessMinutes = 1;
    public const int MaxFreshnessMinutes = 525_600;
    public const int HashLength = 64;

    private static readonly AttributeOperator[] EqualityOperators =
    [
        AttributeOperator.Equals, AttributeOperator.NotEquals, AttributeOperator.In, AttributeOperator.NotIn,
        AttributeOperator.IsEmpty, AttributeOperator.IsNotEmpty,
    ];

    private static readonly AttributeOperator[] OrderedOperators =
    [
        .. EqualityOperators,
        AttributeOperator.GreaterThan, AttributeOperator.GreaterThanOrEqual, AttributeOperator.LessThan, AttributeOperator.LessThanOrEqual,
    ];

    private static readonly AttributeOperator[] BooleanOperators =
    [
        AttributeOperator.Equals, AttributeOperator.NotEquals, AttributeOperator.IsEmpty, AttributeOperator.IsNotEmpty,
    ];

    /// <summary>Operators a rule may use with <paramref name="dataType"/>. Codes and text are not ordered.</summary>
    public static IReadOnlyList<AttributeOperator> AllowedOperators(AttributeDataType dataType) => dataType switch
    {
        AttributeDataType.Code or AttributeDataType.Text => EqualityOperators,
        AttributeDataType.Integer or AttributeDataType.Decimal or AttributeDataType.Date or AttributeDataType.Timestamp => OrderedOperators,
        AttributeDataType.Boolean => BooleanOperators,
        _ => [],
    };

    /// <summary>True for types whose values are strings bounded by <see cref="AttributeDraft.MaxLength"/>.</summary>
    public static bool IsStringType(AttributeDataType dataType) => dataType is AttributeDataType.Code or AttributeDataType.Text;

    /// <summary>Lower-case snake_case key starting with a letter, for example <c>ee_subgroup_code</c>.</summary>
    public static void ValidateKey(string? key)
    {
        if (key is null || key.Length > KeyMaxLength || !KeyPattern().IsMatch(key))
        {
            throw new AttributeDefinitionRejectedException("Key", "must be lower-case letters, digits or '_', start with a letter and be 2 to 64 characters");
        }
    }

    /// <summary>Checks every field of a draft. Throws <see cref="AttributeDefinitionRejectedException"/>.</summary>
    public static void ValidateDraft(AttributeDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.DisplayName) || draft.DisplayName.Length > DisplayNameMaxLength
            || draft.DisplayName != draft.DisplayName.Trim() || draft.DisplayName.Any(IsForbiddenCharacter)
            || !draft.DisplayName.IsNormalized(System.Text.NormalizationForm.FormC))
        {
            throw new AttributeDefinitionRejectedException(
                "DisplayName", $"is required, at most {DisplayNameMaxLength} characters, NFC-normalized, without surrounding spaces, control, invisible, private-use or unassigned characters");
        }

        Defined(draft.DataType, "DataType");
        Defined(draft.SubjectScope, "SubjectScope");
        Defined(draft.Sensitivity, "Sensitivity");
        Defined(draft.Normalization, "Normalization");

        if (draft.SourceSystem is null || draft.SourceSystem.Length > SourceSystemMaxLength || !SourcePattern().IsMatch(draft.SourceSystem))
        {
            throw new AttributeDefinitionRejectedException("SourceSystem", "must be a lower-case code such as 'successfactors'");
        }

        if (draft.FreshnessMaxAgeMinutes is < MinFreshnessMinutes or > MaxFreshnessMinutes)
        {
            throw new AttributeDefinitionRejectedException("FreshnessMaxAgeMinutes", $"must be between {MinFreshnessMinutes} and {MaxFreshnessMinutes}");
        }

        if (IsStringType(draft.DataType))
        {
            if (draft.MaxLength is null or < 1 or > MaxValueLength)
            {
                throw new AttributeDefinitionRejectedException("MaxLength", $"is required for codes and text and must be between 1 and {MaxValueLength}");
            }
        }
        else
        {
            if (draft.MaxLength is not null)
            {
                throw new AttributeDefinitionRejectedException("MaxLength", "applies only to codes and text");
            }

            if (draft.Normalization != AttributeNormalization.None)
            {
                throw new AttributeDefinitionRejectedException("Normalization", "applies only to codes and text");
            }
        }

        var operators = draft.Operators ?? throw new AttributeDefinitionRejectedException("Operators", "are required");
        if (operators.Count == 0)
        {
            throw new AttributeDefinitionRejectedException("Operators", "must contain at least one operator");
        }

        if (operators.Distinct().Count() != operators.Count)
        {
            throw new AttributeDefinitionRejectedException("Operators", "must not repeat an operator");
        }

        var allowed = AllowedOperators(draft.DataType);
        foreach (var op in operators)
        {
            if (!Enum.IsDefined(op) || !allowed.Contains(op))
            {
                throw new AttributeDefinitionRejectedException("Operators", $"contain an operator not allowed for {draft.DataType}");
            }
        }
    }

    internal static bool IsForbiddenCharacter(char c) =>
        char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
            or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse
            or System.Globalization.UnicodeCategory.OtherNotAssigned;

    private static void Defined<TEnum>(TEnum value, string field)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new AttributeDefinitionRejectedException(field, "is not a defined value");
        }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{1,63}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,63}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SourcePattern();
}

/// <summary>The editable content of an attribute definition version.</summary>
public sealed record AttributeDraft(
    string DisplayName,
    AttributeDataType DataType,
    AttributeSubjectScope SubjectScope,
    string SourceSystem,
    AttributeSensitivity Sensitivity,
    AttributeNormalization Normalization,
    int FreshnessMaxAgeMinutes,
    int? MaxLength,
    IReadOnlyList<AttributeOperator> Operators);
