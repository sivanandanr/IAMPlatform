using System.Diagnostics.CodeAnalysis;

namespace IAM.Modules.Policy.Domain.Rules;

/// <summary>Data type of an attribute a rule can test (IAM-WF-014). Same names as the Identity catalog (ADR-0015).</summary>
public enum RuleDataType
{
    Code = 1,
    Text = 2,

    [SuppressMessage("Naming", "CA1720", Justification = "Catalog data type names are stored and exchanged by name (IAM-WF-014).")]
    Integer = 3,

    [SuppressMessage("Naming", "CA1720", Justification = "Catalog data type names are stored and exchanged by name (IAM-WF-014).")]
    Decimal = 4,
    Date = 5,
    Boolean = 6,
    Timestamp = 7,
}

/// <summary>The allowlisted operators (IAM-WF-015, ADR-0004). No regex, StartsWith or Contains; new ones are engineering changes.</summary>
public enum RuleOperator
{
    Equals = 1,
    NotEquals = 2,
    In = 3,
    NotIn = 4,
    IsEmpty = 5,
    IsNotEmpty = 6,
    GreaterThan = 7,
    GreaterThanOrEqual = 8,
    LessThan = 9,
    LessThanOrEqual = 10,
}

/// <summary>Whose attribute a condition reads. Never defaulted: a rule must say beneficiary or requester (spec L.7).</summary>
public enum RuleSubject
{
    Beneficiary = 1,
    Requester = 2,
    Organization = 3,
    Application = 4,
    Entitlement = 5,
    Request = 6,
}

/// <summary>Sensitivity of an attribute; Confidential and Restricted values never enter evidence (IAM-WF-020).</summary>
public enum RuleSensitivity
{
    Public = 1,
    Internal = 2,
    Confidential = 3,
    Restricted = 4,
}

/// <summary>Declared normalisation of an attribute (DL-006). Nothing strips leading zeros or converts codes to numbers.</summary>
public enum RuleNormalization
{
    None = 1,
    Trim = 2,
    TrimUpperInvariant = 3,
}

/// <summary>AND or OR of a group node.</summary>
public enum RuleGroupOperator
{
    And = 1,
    Or = 2,
}

/// <summary>Exact-name lookups for the rule document. <see cref="Enum.Parse(Type, string)"/> is avoided: it accepts numbers and other forms.</summary>
public static class RuleVocabulary
{
    private static readonly RuleOperator[] EqualityOperators =
    [
        RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.In, RuleOperator.NotIn,
        RuleOperator.IsEmpty, RuleOperator.IsNotEmpty,
    ];

    private static readonly RuleOperator[] OrderedOperators =
    [
        .. EqualityOperators,
        RuleOperator.GreaterThan, RuleOperator.GreaterThanOrEqual, RuleOperator.LessThan, RuleOperator.LessThanOrEqual,
    ];

    private static readonly RuleOperator[] BooleanOperators =
    [
        RuleOperator.Equals, RuleOperator.NotEquals, RuleOperator.IsEmpty, RuleOperator.IsNotEmpty,
    ];

    private static readonly Dictionary<string, RuleOperator> Operators = Names<RuleOperator>();
    private static readonly Dictionary<string, RuleSubject> Subjects = Names<RuleSubject>();
    private static readonly Dictionary<string, RuleDataType> DataTypes = Names<RuleDataType>();
    private static readonly Dictionary<string, RuleSensitivity> Sensitivities = Names<RuleSensitivity>();
    private static readonly Dictionary<string, RuleNormalization> Normalizations = Names<RuleNormalization>();

    /// <summary>Operators the engine allows for a data type. Codes and text are not ordered (ADR-0004).</summary>
    public static IReadOnlyList<RuleOperator> AllowedOperators(RuleDataType dataType) => dataType switch
    {
        RuleDataType.Code or RuleDataType.Text => EqualityOperators,
        RuleDataType.Integer or RuleDataType.Decimal or RuleDataType.Date or RuleDataType.Timestamp => OrderedOperators,
        RuleDataType.Boolean => BooleanOperators,
        _ => [],
    };

    public static bool TryOperator(string? name, out RuleOperator value) => Lookup(Operators, name, out value);

    public static bool TrySubject(string? name, out RuleSubject value) => Lookup(Subjects, name, out value);

    public static bool TryDataType(string? name, out RuleDataType value) => Lookup(DataTypes, name, out value);

    public static bool TrySensitivity(string? name, out RuleSensitivity value) => Lookup(Sensitivities, name, out value);

    public static bool TryNormalization(string? name, out RuleNormalization value) => Lookup(Normalizations, name, out value);

    private static bool Lookup<TEnum>(Dictionary<string, TEnum> names, string? name, out TEnum value)
        where TEnum : struct, Enum
    {
        if (name is not null && names.TryGetValue(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static Dictionary<string, TEnum> Names<TEnum>()
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().ToDictionary(v => v.ToString(), v => v, StringComparer.Ordinal);
}

/// <summary>
/// Complexity limits. <b>Provisional (DL-008):</b> the spec leaves them open (L.6); these are the ADR-0004 proposals
/// pending business and risk sign-off. The time limit is a step budget rather than a clock, so replaying the same inputs
/// always gives the same result on any machine (IAM-WF-022).
/// </summary>
public static class RuleLimits
{
    public const int MaxGroupDepth = 4;
    public const int MaxConditionsPerRule = 32;
    public const int MaxListValues = 256;
    public const int MaxLiteralLength = 256;
    public const int MaxRuleBytes = 32 * 1024;
    public const int MaxRulesPerVersion = 1_000;
    public const int MaxPriority = 1_000_000;

    /// <summary>
    /// Nodes visited per evaluation: exactly what a maximal version needs (each rule has at most 32 conditions and 97
    /// groups, a root plus three per condition). A guard, so a compiled version cannot reach it; the limits above are what
    /// bound the work. It counts nodes, not time.
    /// </summary>
    public const int MaxEvaluationSteps = MaxRulesPerVersion * (1 + (MaxGroupDepth * MaxConditionsPerRule));

    /// <summary>Most overlaps reported for one version; more sets a truncated flag, which never reads as clean.</summary>
    public const int MaxOverlapsReported = 1_000;

    /// <summary>The rule document version this code understands (ADR-0004: operator semantics change only with a new one).</summary>
    public const int SchemaVersion = 1;
}

/// <summary>Why a rule or policy version was refused. Stable codes for the later 422 response.</summary>
public enum RuleRejection
{
    TooLarge = 1,
    InvalidJson = 2,
    DuplicateProperty = 3,
    UnknownProperty = 4,
    MissingProperty = 5,
    InvalidPropertyType = 6,
    UnsupportedSchemaVersion = 7,
    UnknownNodeType = 8,
    UnknownGroupOperator = 9,
    UnknownOperator = 10,
    UnknownSubject = 11,
    InvalidAttributeId = 12,
    EmptyGroup = 13,
    TooDeep = 14,
    TooManyConditions = 15,
    TooManyValues = 16,
    LiteralTooLong = 17,
    UnknownAttribute = 18,
    SubjectNotAllowed = 19,
    OperatorNotAllowed = 20,
    WrongValueCount = 21,
    InvalidLiteral = 22,
    DuplicateLiteral = 23,
    WildcardNotAllowed = 24,
    TooManyRules = 25,
    EmptyPolicy = 26,
    DuplicateRuleId = 27,
    InvalidPriority = 28,
    InvalidEffectivePeriod = 29,
    InvalidAttributeMetadata = 30,
    AttributeTypeNotAllowed = 31,
}

/// <summary>A rule was refused. The message names the rejection and where it is, never a value from the document.</summary>
public sealed class RuleRejectedException : Exception
{
    public RuleRejectedException()
    {
    }

    public RuleRejectedException(string message)
        : base(message)
    {
    }

    public RuleRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RuleRejectedException(RuleRejection rejection, string path, Exception? innerException = null)
        : base($"Rule rejected: {rejection} at {path}.", innerException)
    {
        Rejection = rejection;
        Path = path;
    }

    public RuleRejection? Rejection { get; }

    public string? Path { get; }
}
