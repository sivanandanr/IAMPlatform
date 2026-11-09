using System.Globalization;
using System.Text.RegularExpressions;

namespace IAM.Modules.Identity.Domain.Attributes;

/// <summary>A raw value checked against an attribute definition: either a typed value with its canonical text, or an error.</summary>
public sealed record AttributeValueResult(bool IsValid, object? Value, string? Canonical, string? Error)
{
    public static AttributeValueResult Valid(object value, string canonical) => new(true, value, canonical, null);

    public static AttributeValueResult Invalid(string error) => new(false, null, null, error);
}

/// <summary>
/// Turns raw source text into a typed attribute value (IAM-WF-014, DL-006). Codes are exact strings: only the
/// attribute's declared normalisation is applied, nothing strips leading zeros or converts to numbers, so "01" and "1"
/// stay different. Errors describe the rule, never the value.
/// </summary>
public static partial class AttributeValueValidator
{
    public static AttributeValueResult Validate(AttributeDraft definition, string? raw)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (raw is null)
        {
            return AttributeValueResult.Invalid("value is missing");
        }

        // Bound the work done on untrusted input before trimming or case-mapping it.
        if (raw.Length > MaxRawLength)
        {
            return AttributeValueResult.Invalid($"is longer than {MaxRawLength} characters");
        }

        // A code stays ASCII before upper-casing: otherwise U+017F (long s) would become a valid "S".
        if (definition.DataType == AttributeDataType.Code && definition.Normalization == AttributeNormalization.TrimUpperInvariant
            && !raw.Trim().All(char.IsAscii))
        {
            return AttributeValueResult.Invalid($"must be a code of 1 to {definition.MaxLength ?? 0} letters, digits or '_', '-', '.', '/'");
        }

        var text = definition.Normalization switch
        {
            AttributeNormalization.None => raw,
            AttributeNormalization.Trim => raw.Trim(),
            AttributeNormalization.TrimUpperInvariant => raw.Trim().ToUpperInvariant(),
            _ => null,
        };
        if (text is null)
        {
            return AttributeValueResult.Invalid("normalization is not defined");
        }

        return definition.DataType switch
        {
            AttributeDataType.Code => CodeValue(text, definition.MaxLength ?? 0),
            AttributeDataType.Text => TextValue(text, definition.MaxLength ?? 0),
            AttributeDataType.Integer => IntegerPattern().IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)
                ? AttributeValueResult.Valid(l, l.ToString(CultureInfo.InvariantCulture))
                : AttributeValueResult.Invalid("must be a whole number without separators"),
            AttributeDataType.Decimal => DecimalPattern().IsMatch(text) && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
                ? AttributeValueResult.Valid(d, CanonicalDecimal(d))
                : AttributeValueResult.Invalid("must be a decimal number with at most 20 integer and 8 fraction digits"),
            AttributeDataType.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? AttributeValueResult.Valid(date, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : AttributeValueResult.Invalid("must be a date in the form yyyy-MM-dd"),
            AttributeDataType.Boolean => text switch
            {
                "true" => AttributeValueResult.Valid(true, "true"),
                "false" => AttributeValueResult.Valid(false, "false"),
                _ => AttributeValueResult.Invalid("must be 'true' or 'false'"),
            },
            AttributeDataType.Timestamp => TimestampPattern().IsMatch(text)
                && DateTimeOffset.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)
                ? AttributeValueResult.Valid(ts, ts.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture))
                : AttributeValueResult.Invalid("must be a UTC timestamp in the form yyyy-MM-ddTHH:mm:ss[.ffffff]Z"),
            _ => AttributeValueResult.Invalid("data type is not defined"),
        };
    }

    private static AttributeValueResult CodeValue(string text, int maxLength)
    {
        if (text.Length == 0 || text.Length > maxLength || !CodePattern().IsMatch(text))
        {
            return AttributeValueResult.Invalid($"must be a code of 1 to {maxLength} letters, digits or '_', '-', '.', '/'");
        }

        return AttributeValueResult.Valid(text, text);
    }

    private static AttributeValueResult TextValue(string text, int maxLength)
    {
        // Empty is not a value: a missing source value must be absent, never "" (so NotEquals/NotIn cannot match it).
        if (text.Length == 0 || text.Length > maxLength || text.Any(AttributeRules.IsForbiddenCharacter))
        {
            return AttributeValueResult.Invalid($"must be 1 to {maxLength} characters without control or invisible characters");
        }

        return AttributeValueResult.Valid(text, text);
    }

    /// <summary>One text per number: trailing zeros removed, so "12.50" and "12.5" are the same; zero is always "0".</summary>
    private static string CanonicalDecimal(decimal value) =>
        value == 0m ? "0" : value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>Raw input longer than this is refused before any normalization.</summary>
    public const int MaxRawLength = 4 * AttributeRules.MaxValueLength;

    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.f'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
    ];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_\-./]*\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^-?[0-9]{1,18}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^-?[0-9]{1,20}(\.[0-9]{1,8})?\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex DecimalPattern();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?Z\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TimestampPattern();
}
