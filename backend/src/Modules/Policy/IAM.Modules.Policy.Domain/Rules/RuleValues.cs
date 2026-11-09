using System.Globalization;

namespace IAM.Modules.Policy.Domain.Rules;

/// <summary>
/// What a rule needs to know about a published attribute version. Policy's own type: Domain may not reference Identity
/// (ADR-0001); the Application layer maps the Identity contract onto it (<c>AttributeSpecMapper</c>).
/// </summary>
public sealed record AttributeSpec(
    Guid AttributeId,
    Guid VersionId,
    string ContentHash,
    RuleSubject Scope,
    RuleDataType DataType,
    RuleSensitivity Sensitivity,
    RuleNormalization Normalization,
    int FreshnessMaxAgeMinutes,
    int? MaxLength,
    IReadOnlySet<RuleOperator> Operators);

/// <summary>A value parsed for an attribute's type. <see cref="Canonical"/> is unique per value, so equality compares it directly.</summary>
internal sealed record TypedValue(string Canonical, IComparable Comparable)
{
    /// <summary>The value may be sensitive; a logged record must not show it.</summary>
    public override string ToString() => nameof(TypedValue);
}

/// <summary>
/// Parses a value or literal the way the catalog does (Identity's AttributeValueValidator, DL-006): only the declared
/// normalisation, no zero-stripping, no coercion. Policy keeps its own copy because Domain layers cannot share code
/// across modules; a test checks the two agree on a shared corpus. Values are never echoed. The formats are checked by
/// plain character scans, not regexes: a regex timeout would make the result depend on how busy the machine is, and
/// replay must give the same answer (IAM-WF-022).
/// </summary>
internal static class AttributeValueCodec
{
    /// <summary>Longest raw text looked at, as in the catalog; anything longer is refused before normalising.</summary>
    public const int MaxRawLength = 1600;

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

    public static bool TryParse(AttributeSpec spec, string? raw, out TypedValue value)
    {
        ArgumentNullException.ThrowIfNull(spec);
        value = null!;
        if (raw is null || raw.Length > MaxRawLength)
        {
            return false;
        }

        var text = spec.Normalization switch
        {
            RuleNormalization.None => raw,
            RuleNormalization.Trim => raw.Trim(),

            // A code stays ASCII before upper-casing: otherwise U+017F (long s) would become a valid "S".
            RuleNormalization.TrimUpperInvariant when spec.DataType == RuleDataType.Code && !raw.Trim().All(char.IsAscii) => null,
            RuleNormalization.TrimUpperInvariant => raw.Trim().ToUpperInvariant(),
            _ => null,
        };
        if (text is null)
        {
            return false;
        }

        switch (spec.DataType)
        {
            case RuleDataType.Code when text.Length > 0 && text.Length <= (spec.MaxLength ?? 0) && IsCode(text):
            case RuleDataType.Text when text.Length > 0 && text.Length <= (spec.MaxLength ?? 0) && !text.Any(IsForbidden):
                value = new TypedValue(text, text);
                return true;
            case RuleDataType.Integer when IsInteger(text)
                && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole):
                value = new TypedValue(whole.ToString(CultureInfo.InvariantCulture), whole);
                return true;
            case RuleDataType.Decimal when IsDecimal(text)
                && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number):
                value = new TypedValue(CanonicalDecimal(number), number);
                return true;
            case RuleDataType.Date when DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                value = new TypedValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), date);
                return true;
            case RuleDataType.Boolean when text is "true" or "false":
                value = new TypedValue(text, text == "true");
                return true;
            case RuleDataType.Timestamp when IsTimestamp(text)
                && DateTimeOffset.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment):
                value = new TypedValue(moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture), moment);
                return true;
            default:
                return false;
        }
    }

    private static string CanonicalDecimal(decimal value) =>
        value == 0m ? "0" : value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static bool IsForbidden(char c) =>
        char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned;

    /// <summary>An ASCII letter or digit, then letters, digits, '_', '-', '.' or '/'.</summary>
    private static bool IsCode(string text) =>
        char.IsAsciiLetterOrDigit(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/');

    /// <summary>1 to <paramref name="maxDigits"/> ASCII digits and nothing else.</summary>
    private static bool IsDigits(ReadOnlySpan<char> text, int maxDigits) =>
        text.Length >= 1 && text.Length <= maxDigits && !text.ContainsAnyExceptInRange('0', '9');

    private static bool IsInteger(string text) =>
        IsDigits(text.StartsWith('-') ? text.AsSpan(1) : text, 18);

    private static bool IsDecimal(string text)
    {
        var body = text.StartsWith('-') ? text.AsSpan(1) : text.AsSpan();
        var point = body.IndexOf('.');
        return point < 0 ? IsDigits(body, 20) : IsDigits(body[..point], 20) && IsDigits(body[(point + 1)..], 8);
    }

    /// <summary>yyyy-MM-ddTHH:mm:ss[.f to ffffff]Z with ASCII digits; the calendar is checked by the parse that follows.</summary>
    private static bool IsTimestamp(string text)
    {
        if (text.Length < 20 || text.Length > 27 || text[^1] != 'Z' || text[4] != '-' || text[7] != '-' || text[10] != 'T' || text[13] != ':' || text[16] != ':')
        {
            return false;
        }

        var span = text.AsSpan();
        return IsDigits(span[..4], 4) && IsDigits(span[5..7], 2) && IsDigits(span[8..10], 2) && IsDigits(span[11..13], 2)
            && IsDigits(span[14..16], 2) && IsDigits(span[17..19], 2)
            && (text.Length == 20 || (text[19] == '.' && IsDigits(span[20..^1], 6)));
    }
}

/// <summary>How much is known about an attribute's value at evaluation time.</summary>
public enum AttributeValueState
{
    /// <summary>A fresh, valid value.</summary>
    Present = 1,

    /// <summary>The source was read recently and has no value for this attribute.</summary>
    Empty = 2,

    /// <summary>No entry at all: nothing is known, which is not the same as empty.</summary>
    NotSynchronized = 3,

    /// <summary>Older than the attribute's freshness limit, or dated after the evaluation time.</summary>
    Stale = 4,

    /// <summary>Not a valid value for the attribute's type.</summary>
    Invalid = 5,

    /// <summary>The snapshot holds more than one entry for this attribute.</summary>
    Conflicted = 6,
}

/// <summary>One attribute value in a snapshot: a null value means the source says there is none.</summary>
public sealed record SnapshotEntry(RuleSubject Subject, Guid AttributeId, string? Value, DateTimeOffset SynchronizedAtUtc)
{
    /// <summary>The value may be Restricted; a logged record must not show it.</summary>
    public override string ToString() => $"{Subject} {AttributeId:D} at {SynchronizedAtUtc:O}";
}

/// <summary>
/// The validated local attribute snapshot a decision is made from (IAM-WF-022): rules never call HR or SAP. Immutable, so
/// replaying with the same snapshot gives the same result. Duplicate entries are kept as a conflict, never a choice.
/// </summary>
public sealed class AttributeSnapshot
{
    private readonly Dictionary<(RuleSubject Subject, Guid AttributeId), SnapshotEntry> _entries = [];
    private readonly HashSet<(RuleSubject Subject, Guid AttributeId)> _conflicts = [];

    public AttributeSnapshot(IEnumerable<SnapshotEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            var key = (entry.Subject, entry.AttributeId);
            if (!_entries.TryAdd(key, entry))
            {
                _conflicts.Add(key);
            }
        }
    }

    /// <summary>Reads a value for a rule. <paramref name="value"/> is set only when the state is <see cref="AttributeValueState.Present"/>.</summary>
    internal AttributeValueState Read(RuleSubject subject, AttributeSpec spec, DateTimeOffset asOfUtc, out TypedValue? value)
    {
        ArgumentNullException.ThrowIfNull(spec);
        value = null;
        var key = (subject, spec.AttributeId);
        if (_conflicts.Contains(key))
        {
            return AttributeValueState.Conflicted;
        }

        if (!_entries.TryGetValue(key, out var entry))
        {
            return AttributeValueState.NotSynchronized;
        }

        // A future date cannot be trusted. The limit is inclusive: exactly the maximum age is still fresh.
        if (entry.SynchronizedAtUtc > asOfUtc || asOfUtc - entry.SynchronizedAtUtc > TimeSpan.FromMinutes(spec.FreshnessMaxAgeMinutes))
        {
            return AttributeValueState.Stale;
        }

        if (entry.Value is null)
        {
            return AttributeValueState.Empty;
        }

        if (!AttributeValueCodec.TryParse(spec, entry.Value, out var parsed))
        {
            return AttributeValueState.Invalid;
        }

        value = parsed;
        return AttributeValueState.Present;
    }
}
