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

    /// <summary>A target id in lower case: directories compare them ignoring case, so this is what must be unique (DL-098).</summary>
    public static string TargetIdKey(string targetId) => targetId.ToLowerInvariant();

    /// <summary>The target's own identifier (a distinguished name, a GUID): visible text without leading or trailing spaces.</summary>
    public static void ValidateTargetId(string? targetId)
    {
        if (!SecondPersonText.IsText(targetId, TargetIdMaxLength) || targetId!.Trim().Length != targetId.Length)
        {
            throw new AccountRejectedException("TargetId", $"must be visible text of at most {TargetIdMaxLength} characters, without leading or trailing spaces");
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
