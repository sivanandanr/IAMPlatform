using IAM.Modules.Account.Domain.Accounts;
using IAM.Modules.Account.Domain.Naming;

namespace IAM.Modules.Account.Application.Accounts;

public sealed record AccountView(
    Guid Id, Guid ApplicationId, string Type, string Name, string? TargetId, string Owner, string Status, string Origin, Guid? LinkRequestId, bool Verified);

public sealed record AccountLinkRequestView(
    Guid Id,
    Guid ApplicationId,
    string Type,
    string Name,
    string TargetId,
    string Owner,
    string Maker,
    string Reason,
    DateTimeOffset ProposedAtUtc,
    string State,
    string? Checker,
    string? DecisionReason,
    DateTimeOffset? DecidedAtUtc,
    Guid? AccountId);

/// <summary>One part of a naming template as the API receives it: every value as text.</summary>
public sealed record NamePartText(string? Literal, string? Attribute, IReadOnlyList<string?>? Transforms, int? Length);

public sealed record NamePartView(string? Literal, string? Attribute, IReadOnlyList<string> Transforms, int? Length);

public sealed record NamingRuleVersionView(
    Guid VersionId, int VersionNumber, string State, IReadOnlyList<NamePartView> Parts, int MaxLength, string ContentHash, string EditedBy, long ConcurrencyVersion);

public sealed record NamingRuleView(Guid Id, Guid ApplicationId, string AccountType, NamingRuleVersionView? Active, NamingRuleVersionView? Draft, long ConcurrencyVersion);

public sealed record NamingRuleChangeRequestView(
    Guid Id,
    Guid NamingRuleId,
    int VersionNumber,
    string ContentHash,
    string Maker,
    string Reason,
    DateTimeOffset ProposedAtUtc,
    string State,
    string? Checker,
    string? DecisionReason,
    DateTimeOffset? DecidedAtUtc);

public sealed record NamingRuleChangeReview(NamingRuleChangeRequestView Request, NamingRuleVersionView? Draft);

/// <summary>
/// Account administration (DL-089): linking existing accounts and naming rules, each decided by a second person. Every write
/// checks the caller's permission and is audited in the same transaction, refusals included.
/// </summary>
public interface IAccountAdministration
{
    Task<AccountView?> FindAccountAsync(Guid accountId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountView>> ListAccountsAsync(Guid applicationId, string? owner, CancellationToken cancellationToken);

    Task<AccountLinkRequestView> ProposeLinkAsync(Guid applicationId, string type, string name, string targetId, string owner, string reason, CancellationToken cancellationToken);

    Task<AccountLinkRequestView?> FindLinkRequestAsync(Guid requestId, CancellationToken cancellationToken);

    Task<AccountLinkRequestView> ApproveLinkAsync(Guid requestId, string? reason, CancellationToken cancellationToken);

    Task<AccountLinkRequestView> RejectLinkAsync(Guid requestId, string reason, CancellationToken cancellationToken);

    Task<AccountLinkRequestView> WithdrawLinkAsync(Guid requestId, string? reason, CancellationToken cancellationToken);

    Task<NamingRuleView?> FindNamingRuleAsync(Guid applicationId, string accountType, CancellationToken cancellationToken);

    /// <summary>Creates the rule with a first draft, replaces the draft, or starts the next draft after the active version.</summary>
    Task<NamingRuleView> SetNamingDraftAsync(Guid applicationId, string accountType, long? expectedConcurrencyVersion, IReadOnlyList<NamePartText?> parts, int maxLength, CancellationToken cancellationToken);

    Task<NamingRuleChangeRequestView> ProposeNamingRuleAsync(Guid applicationId, string accountType, string reason, CancellationToken cancellationToken);

    Task<NamingRuleChangeReview?> ReviewNamingRequestAsync(Guid requestId, CancellationToken cancellationToken);

    Task<NamingRuleChangeRequestView> ApproveNamingRuleAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken);

    Task<NamingRuleChangeRequestView> RejectNamingRuleAsync(Guid requestId, string reason, CancellationToken cancellationToken);

    Task<NamingRuleChangeRequestView> WithdrawNamingRuleAsync(Guid requestId, string? reason, CancellationToken cancellationToken);
}

/// <summary>How a caller-facing layer should treat an account failure, without naming domain types.</summary>
public enum AccountFailureKind
{
    None = 0,
    InvalidField = 1,
    Forbidden = 2,
    WrongState = 3,
    Invalid = 4,
    Conflict = 5,
    NotFound = 6,
    Unavailable = 7,
}

public static class AccountApi
{
    public static AccountType ParseType(string? text) => Parse<AccountType>(text, "Type");

    /// <summary>Builds a template from API text; enum values must be exact names. The domain checks every rule again.</summary>
    public static NamingTemplate ToTemplate(IReadOnlyList<NamePartText?>? parts, int maxLength)
    {
        if (parts is null || parts.Count > NamingTemplates.MaxParts || parts.Any(p => p is null))
        {
            throw new AccountRejectedException("Parts", $"must be 1-{NamingTemplates.MaxParts} parts");
        }

        return new NamingTemplate(
            [.. parts.Select(p => new NamePart(
                p!.Literal,
                p.Attribute,
                p.Transforms is null || p.Transforms.Count > NamingTemplates.MaxTransforms
                    ? throw new AccountRejectedException("Transforms", $"must be a list of at most {NamingTemplates.MaxTransforms}")
                    : [.. p.Transforms.Select(t => Parse<NameTransform>(t, "Transforms"))],
                p.Length))],
            maxLength);
    }

    public static AccountFailureKind Classify(Exception exception, out string? code, out string? field, out string? rule)
    {
        code = null;
        field = null;
        rule = null;
        switch (exception)
        {
            case AccountRejectedException rejected:
                field = rejected.Field;
                rule = rejected.Message;
                return AccountFailureKind.InvalidField;
            case AccountConflictException:
                return AccountFailureKind.Conflict;
            case AccountNotFoundException:
                return AccountFailureKind.NotFound;
            case AccountRefusedException refused:
                code = refused.Refusal?.ToString();
                return refused.Refusal switch
                {
                    AccountRefusal.NotAllowed or AccountRefusal.MakerCannotDecide or AccountRefusal.OwnerCannotDecide or AccountRefusal.NotMaker
                        or AccountRefusal.MakerNoLongerAllowed => AccountFailureKind.Forbidden,
                    AccountRefusal.NotProposed or AccountRefusal.AlreadyPending or AccountRefusal.NoDraft or AccountRefusal.DraftChanged
                        or AccountRefusal.ApplicationNotActive or AccountRefusal.PrimaryExists or AccountRefusal.NameInUse
                        or AccountRefusal.TargetInUse => AccountFailureKind.WrongState,
                    AccountRefusal.InvalidRecord or null => AccountFailureKind.Unavailable,
                    _ => AccountFailureKind.Invalid,
                };
            default:
                return AccountFailureKind.None;
        }
    }

    private static TEnum Parse<TEnum>(string? text, string field)
        where TEnum : struct, Enum =>
        text is not null && Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal)
            ? Enum.Parse<TEnum>(text)
            : throw new AccountRejectedException(field, $"must be one of {string.Join(", ", Enum.GetNames<TEnum>())}");
}

public sealed class AccountNotFoundException : Exception
{
    public AccountNotFoundException()
        : base("The account, request or naming rule was not found.")
    {
    }

    public AccountNotFoundException(string message)
        : base(message)
    {
    }

    public AccountNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Someone else changed the same thing first; reload and try again.</summary>
public sealed class AccountConflictException : Exception
{
    public AccountConflictException()
        : base("Changed by someone else; reload and try again.")
    {
    }

    public AccountConflictException(string message)
        : base(message)
    {
    }

    public AccountConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
