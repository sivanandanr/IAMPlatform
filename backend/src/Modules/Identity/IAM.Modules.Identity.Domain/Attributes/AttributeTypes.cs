using System.Diagnostics.CodeAnalysis;

namespace IAM.Modules.Identity.Domain.Attributes;

/// <summary>Data type of a catalog attribute (IAM-WF-014). Stored by name, never by number.</summary>
public enum AttributeDataType
{
    /// <summary>An exact string code, for example an HR code: "01" and "1" are different values (DL-006).</summary>
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

/// <summary>Comparison operators a rule may use with an attribute (IAM-WF-015). Each data type allows a fixed subset.</summary>
public enum AttributeOperator
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

/// <summary>Whose attribute it is (IAM-WF-014 "subject scope", spec L.1).</summary>
public enum AttributeSubjectScope
{
    Beneficiary = 1,
    Requester = 2,
    Organization = 3,
    Application = 4,
    Entitlement = 5,
    Request = 6,
}

/// <summary>Sensitivity classification; drives masking in audit and evidence (IAM-WF-020).</summary>
public enum AttributeSensitivity
{
    Public = 1,
    Internal = 2,
    Confidential = 3,
    Restricted = 4,
}

/// <summary>
/// How a raw value is normalised before validation and comparison. Declared per attribute (DL-006); the default is
/// <see cref="None"/>. Nothing ever strips leading zeros or converts codes to numbers.
/// </summary>
public enum AttributeNormalization
{
    None = 1,

    /// <summary>Remove leading and trailing white space.</summary>
    Trim = 2,

    /// <summary>Trim, then upper-case with invariant culture.</summary>
    TrimUpperInvariant = 3,
}

/// <summary>Lifecycle of one version of an attribute definition (IAM-WF-019).</summary>
public enum AttributeVersionState
{
    /// <summary>Editable by its maker; never visible to rules.</summary>
    Draft = 1,

    /// <summary>Immutable and visible to rules. Reached only through an approved publish request (DL-007, DL-064).</summary>
    Published = 2,

    /// <summary>Immutable and no longer usable in new rules.</summary>
    Retired = 3,
}
