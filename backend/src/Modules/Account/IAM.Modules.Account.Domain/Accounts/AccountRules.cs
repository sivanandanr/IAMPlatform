using System.Text;
using System.Text.RegularExpressions;
using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Account.Domain.Accounts;

/// <summary>
/// The account types (spec v3.2 §3.1, DL-084f): Primary is a person's normal account, one per application; Secondary is a
/// separate account for privileged work; Service is used by a system; Shared is used by several people through the vault.
/// Non-human identities are not an account type (DL-085). The names match the catalog's allowed account types.
/// </summary>
public enum AccountType
{
    Primary = 1,
    Secondary = 2,
    Service = 3,
    Shared = 4,
}

/// <summary>Where an account stands. Linked accounts start Active; accounts IAM creates (stage 8 slice 4) start Requested.</summary>
public enum AccountStatus
{
    Requested = 1,
    Active = 2,
    Disabled = 3,
    Closed = 4,
}

/// <summary>How an account came to be recorded: an existing account linked by two people, or one IAM created (slice 4).</summary>
public enum AccountOrigin
{
    Linked = 1,
    Created = 2,
}

/// <summary>Where a naming rule version stands. Only an approved request makes a draft active.</summary>
public enum NamingRuleVersionState
{
    Draft = 1,
    Active = 2,
    Superseded = 3,
}

/// <summary>Why an account change was refused. Codes only, never people or content.</summary>
public enum AccountRefusal
{
    NotAllowed = 1,
    MakerCannotDecide = 2,
    OwnerCannotDecide = 3,
    NotProposed = 4,
    NotMaker = 5,
    InvalidReason = 6,
    InvalidRecord = 7,
    AlreadyPending = 8,
    MakerNoLongerAllowed = 9,

    /// <summary>The application doesn't exist or isn't active.</summary>
    ApplicationNotActive = 10,

    /// <summary>The person already has a live Primary account in this application (spec v3.2 §3.1: one per application).</summary>
    PrimaryExists = 11,

    /// <summary>Another live account of the application has this name (compared ignoring case).</summary>
    NameInUse = 12,

    /// <summary>Another live account of the application has this target id.</summary>
    TargetInUse = 13,

    NoDraft = 14,
    DraftChanged = 15,

    /// <summary>Naming rules exist only for Primary and Secondary accounts; Service and Shared names are given by the requester.</summary>
    NoNamingRuleForType = 16,

    /// <summary>A naming rule names an attribute that isn't published in the attribute catalog.</summary>
    UnknownAttribute = 17,
}

/// <summary>An account change was refused by a rule (see <see cref="AccountRefusal"/>).</summary>
public sealed class AccountRefusedException : Exception
{
    public AccountRefusedException()
    {
    }

    public AccountRefusedException(string message)
        : base(message)
    {
    }

    public AccountRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AccountRefusedException(AccountRefusal refusal, Exception? innerException = null)
        : base($"Account change refused: {refusal}.", innerException)
    {
        Refusal = refusal;
    }

    public AccountRefusal? Refusal { get; }
}

/// <summary>Account input was invalid. The message names the field and rule only, never the value.</summary>
public sealed class AccountRejectedException : Exception
{
    public AccountRejectedException()
    {
    }

    public AccountRejectedException(string message)
        : base(message)
    {
    }

    public AccountRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AccountRejectedException(string field, string rule, Exception? innerException = null)
        : base($"Account input rejected: {field} {rule}.", innerException)
    {
        Field = field;
    }

    public string? Field { get; }
}

/// <summary>Input rules shared by accounts and naming rules.</summary>
public static partial class AccountRules
{
    public const int NameMaxLength = 256;
    public const int TargetIdMaxLength = 512;
    public const int HashLength = 64;

    /// <summary>
    /// An account name (a login): starts with a letter or digit, then letters, digits, dots, underscores or hyphens, at most
    /// 256 characters. Names are names, not access (CLAUDE.md rule 11), but they end up in the target, so they are kept plain.
    /// </summary>
    public static bool IsName(string? name) => name is { Length: >= 1 and <= NameMaxLength } && NamePattern().IsMatch(name);

    public static void ValidateName(string? name, string field = "Name")
    {
        if (!IsName(name))
        {
            throw new AccountRejectedException(field, $"must be 1-{NameMaxLength} letters, digits, dots, underscores or hyphens, starting with a letter or digit");
        }
    }

    /// <summary>The form names are compared in: lower case (invariant), so another spelling of a name is the same name.</summary>
    public static string NameKey(string name) => name.ToLowerInvariant();

    /// <summary>
    /// The form a target id must be unique in (DL-098, DL-099): what a directory would take as the same object. A distinguished
    /// name is read component by component: spaces around separators dropped, escapes decoded, then written back one way, in
    /// lower case. Anything else (a GUID) is lower case. ASCII only, lowered the same way the databases lower it.
    /// </summary>
    public static string TargetIdKey(string targetId)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        return AsciiLower(CanonicalDn(targetId) ?? targetId.Trim());
    }

    /// <summary>Whether text is printable ASCII (space to tilde): target ids are compared the same way in C# and both databases.</summary>
    public static bool IsPrintableAscii(string text) => text.All(c => c is >= ' ' and <= '~');

    /// <summary>The target's own identifier (a distinguished name, a GUID): printable ASCII without leading or trailing spaces.</summary>
    public static void ValidateTargetId(string? targetId)
    {
        if (!SecondPersonText.IsText(targetId, TargetIdMaxLength) || targetId!.Trim().Length != targetId.Length || !IsPrintableAscii(targetId))
        {
            throw new AccountRejectedException("TargetId", $"must be printable ASCII of at most {TargetIdMaxLength} characters, without leading or trailing spaces");
        }

        // One spelling per object (DL-100): a distinguished name this can write one way, or a GUID in its plain form.
        if (targetId.Contains('=', StringComparison.Ordinal) ? CanonicalDn(targetId) is null : !Guid.TryParseExact(targetId, "D", out _))
        {
            throw new AccountRejectedException("TargetId", "must be a distinguished name of named attributes (no quotes, ';', '+' or BER values) or a GUID in the form 00000000-0000-0000-0000-000000000000");
        }
    }

    private static string AsciiLower(string text) =>
        string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });

    /// <summary>
    /// A distinguished name written one way (RFC 4514): <c>type=value</c> components joined by commas, spaces around them dropped,
    /// escapes decoded and only the special characters escaped again. Null if the text isn't a plain DN this can read (a
    /// multi-valued component, a bad escape, a non-ASCII byte): it is then compared as it is.
    /// </summary>
    private static string? CanonicalDn(string text)
    {
        if (!text.Contains('=', StringComparison.Ordinal))
        {
            return null;
        }

        var components = new List<string>();
        var type = new StringBuilder();
        var value = new List<(char Char, bool Escaped)>();
        var inValue = false;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || (text[i] == ',' && inValue))
            {
                if (!inValue || Component(type, value) is not { } component)
                {
                    return null;
                }

                components.Add(component);
                type.Clear();
                value.Clear();
                inValue = false;
                continue;
            }

            var c = text[i];
            if (!inValue)
            {
                if (c == '=')
                {
                    inValue = true;
                }
                else if (c is ',' or '+' or '\\')
                {
                    return null;
                }
                else
                {
                    type.Append(c);
                }

                continue;
            }

            // A multi-valued component, a quoted value or the old ';' separator: not read (DL-100), so refused.
            if (c is '+' or '"' or ';')
            {
                return null;
            }

            if (c != '\\')
            {
                value.Add((c, false));
                continue;
            }

            if (i + 1 >= text.Length)
            {
                return null;
            }

            var next = text[i + 1];
            if (i + 2 < text.Length && Uri.IsHexDigit(next) && Uri.IsHexDigit(text[i + 2]))
            {
                var decoded = (char)Convert.ToByte(text.Substring(i + 1, 2), 16);
                if (decoded is < ' ' or > '~')
                {
                    return null;
                }

                value.Add((decoded, true));
                i += 2;
            }
            else if (next is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=' or '#' or ' ')
            {
                value.Add((next, true));
                i++;
            }
            else
            {
                return null;
            }
        }

        return string.Join(',', components);

        static string? Component(StringBuilder typeText, List<(char Char, bool Escaped)> chars)
        {
            var name = typeText.ToString().Trim(' ');
            // A type by name only (DL-100): an OID such as 2.5.4.3 would be another spelling of CN.
            if (name.Length == 0 || !char.IsAsciiLetter(name[0]) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return null;
            }

            var start = 0;
            var end = chars.Count;
            while (start < end && chars[start] is (' ', false))
            {
                start++;
            }

            while (end > start && chars[end - 1] is (' ', false))
            {
                end--;
            }

            // An unescaped leading '#' is a BER-encoded value, not text (RFC 4514): refused (DL-100).
            if (start == end || chars[start] is ('#', false))
            {
                return null;
            }

            var written = new StringBuilder(name).Append('=');
            for (var k = start; k < end; k++)
            {
                var ch = chars[k].Char;
                var special = ch is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '='
                    || (k == start && ch is '#' or ' ')
                    || (k == end - 1 && ch == ' ');
                if (special)
                {
                    written.Append('\\');
                }

                written.Append(ch);
            }

            return written.ToString();
        }
    }

    public static void ValidateOwner(string? owner)
    {
        if (!SecondPersonText.IsPerson(owner))
        {
            throw new AccountRejectedException("Owner", "must be one person's id");
        }
    }

    public static void ValidateType(AccountType type)
    {
        if (!Enum.IsDefined(type))
        {
            throw new AccountRejectedException("Type", "must be Primary, Secondary, Service or Shared");
        }
    }

    public static bool IsHash(string? hash) =>
        hash is { Length: HashLength } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NamePattern();
}

/// <summary>Runs a shared second-person rule and reports its refusal as an account refusal code.</summary>
internal static class AccountSecondPerson
{
    internal static T Shared<T>(Func<T> rule)
    {
        try
        {
            return rule();
        }
        catch (SecondPersonRefusedException ex)
        {
            throw new AccountRefusedException(Map(ex.Refusal), ex);
        }
    }

    internal static void Shared(Action rule) => Shared(() =>
    {
        rule();
        return true;
    });

    private static AccountRefusal Map(SecondPersonRefusal? refusal) => refusal switch
    {
        SecondPersonRefusal.NotProposed => AccountRefusal.NotProposed,
        SecondPersonRefusal.MakerCannotDecide => AccountRefusal.MakerCannotDecide,
        SecondPersonRefusal.NotMaker => AccountRefusal.NotMaker,
        SecondPersonRefusal.InvalidReason => AccountRefusal.InvalidReason,
        SecondPersonRefusal.InvalidPerson => AccountRefusal.NotAllowed,
        _ => AccountRefusal.InvalidRecord,
    };
}
