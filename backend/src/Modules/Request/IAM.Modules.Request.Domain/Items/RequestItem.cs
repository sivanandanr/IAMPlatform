using System.Text.RegularExpressions;
using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Request.Domain.Items;

/// <summary>Where a request item stands. In this slice an item is frozen at submission; later slices move it on.</summary>
public enum RequestItemState
{
    /// <summary>Frozen and sent for approval.</summary>
    Submitted = 1,
}

/// <summary>Why a request was refused. Codes only, never people or content.</summary>
public enum RequestRefusal
{
    NotAllowed = 1,

    /// <summary>Requesting for someone else needs <c>request.items.request-for-others</c>.</summary>
    NotForOthers = 2,

    /// <summary>The entitlement has no active version, or its application isn't active.</summary>
    EntitlementNotRequestable = 3,

    /// <summary>The account doesn't exist, isn't active, or is in another application.</summary>
    AccountNotUsable = 4,

    /// <summary>The account belongs to someone other than the person getting access.</summary>
    AccountNotBeneficiarys = 5,

    /// <summary>The entitlement doesn't allow this account type (spec v3.2 §2.5).</summary>
    AccountTypeNotAllowed = 6,

    /// <summary>Privileged access never goes on a Primary account (spec v3.2 §2.5).</summary>
    PrivilegedOnPrimary = 7,

    /// <summary>Only the owner of a Service or Shared account can request for it (spec v3.2 §5.3).</summary>
    NotAccountOwner = 8,

    /// <summary>The person already has a live Primary account; the request must name it (spec v3.2 §3.1).</summary>
    PrimaryExists = 9,

    /// <summary>Access for: a duration outside what the entitlement allows, or one given where it fixes the period (spec v3.2 §6.4).</summary>
    InvalidDuration = 10,

    /// <summary>The matrix selected no route: no match, ambiguous, missing or stale data, or no active matrix (CLAUDE.md rule 3).</summary>
    NoRoute = 11,

    /// <summary>A stage of the selected route names something that doesn't resolve (an unknown role type).</summary>
    RouteNotResolvable = 12,

    /// <summary>No name could be proposed for the new account and the route has no level that can give one (DL-091).</summary>
    NoAccountName = 13,

    /// <summary>The same access for the same person is already waiting.</summary>
    AlreadyRequested = 14,

    InvalidRecord = 15,

    /// <summary>The item asks for no new account, so there is no name to give (DL-092).</summary>
    NoNewAccount = 16,

    /// <summary>The stage isn't one of the item's Action stages that may give the account name.</summary>
    NotANameStage = 17,

    /// <summary>The caller can't act on that stage now: it isn't open, or they aren't one of its people (DL-092).</summary>
    NotOnStage = 18,

    /// <summary>A live account of the application already has this name (DL-086).</summary>
    NameInUse = 19,

    /// <summary>The name is already the item's account name.</summary>
    NameUnchanged = 20,
}

/// <summary>A request was refused by a rule (see <see cref="RequestRefusal"/>).</summary>
public sealed class RequestRefusedException : Exception
{
    public RequestRefusedException()
    {
    }

    public RequestRefusedException(string message)
        : base(message)
    {
    }

    public RequestRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RequestRefusedException(RequestRefusal refusal, Exception? innerException = null)
        : base($"Request refused: {refusal}.", innerException)
    {
        Refusal = refusal;
    }

    public RequestRefusal? Refusal { get; }
}

/// <summary>Request input was invalid. The message names the field and rule only, never the value.</summary>
public sealed class RequestRejectedException : Exception
{
    public RequestRejectedException()
    {
    }

    public RequestRejectedException(string message)
        : base(message)
    {
    }

    public RequestRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RequestRejectedException(string field, string rule, Exception? innerException = null)
        : base($"Request input rejected: {field} {rule}.", innerException)
    {
        Field = field;
    }

    public string? Field { get; }
}

/// <summary>What an item asks for an account that doesn't exist yet: its type and, if known, its name (one decision covers both, DL-091b).</summary>
public sealed record NewAccount(string Type, string? Name);

/// <summary>
/// One request item, frozen when submitted (DL-084e, DL-091): who asked, for whom, which entitlement version, which account
/// (an existing one or a new one), for how long, and the route the matrix selected with its evidence. Nothing about it changes
/// afterwards; database triggers refuse any change to these fields.
/// </summary>
public sealed partial class RequestItem
{
    public const int MaxEvidenceLength = 1_048_576;
    public const int MaxRouteLength = 65_536;

    private RequestItem()
    {
        Requester = string.Empty;
        Beneficiary = string.Empty;
        EntitlementContentHash = string.Empty;
        MatrixContentHash = string.Empty;
        PolicyMaker = string.Empty;
        Evidence = string.Empty;
        Route = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Requester { get; private set; }

    public string Beneficiary { get; private set; }

    public Guid ApplicationId { get; private set; }

    public Guid EntitlementId { get; private set; }

    public Guid EntitlementVersionId { get; private set; }

    public string EntitlementContentHash { get; private set; }

    /// <summary>An existing account; null when the item asks for a new one.</summary>
    public Guid? AccountId { get; private set; }

    public string? NewAccountType { get; private set; }

    /// <summary>The new account's name: from the naming rule or the requester (Service, Shared); null until a level gives it (slice 4b).</summary>
    public string? NewAccountName { get; private set; }

    /// <summary>How many days the access lasts; null for no expiry.</summary>
    public int? DurationDays { get; private set; }

    public RequestItemState State { get; private set; }

    public Guid MatrixVersionId { get; private set; }

    public string MatrixContentHash { get; private set; }

    public Guid MatchedRuleId { get; private set; }

    public string PolicyMaker { get; private set; }

    /// <summary>The matrix's canonical evidence (IAM-WF-020, IAM-WF-022).</summary>
    public string Evidence { get; private set; }

    /// <summary>The resolved route as sent to Workflow (canonical JSON of its stages).</summary>
    public string Route { get; private set; }

    public DateTimeOffset SubmittedAtUtc { get; private set; }

    /// <summary>The trace id the item was submitted under, carried to every later step (DL-094); null for older items.</summary>
    public string? CorrelationId { get; private set; }

    /// <summary>Freezes an item from values the service has already checked against the catalog, the accounts and the matrix.</summary>
    public static RequestItem Freeze(
        Guid id,
        string requester,
        string beneficiary,
        Guid applicationId,
        Guid entitlementId,
        Guid entitlementVersionId,
        string entitlementContentHash,
        Guid? accountId,
        NewAccount? newAccount,
        int? durationDays,
        Guid matrixVersionId,
        string matrixContentHash,
        Guid matchedRuleId,
        string policyMaker,
        string evidence,
        string route,
        DateTimeOffset now,
        string? correlationId = null)
    {
        var valid = (correlationId is null || IsTraceId(correlationId)) && id != Guid.Empty && applicationId != Guid.Empty && entitlementId != Guid.Empty && entitlementVersionId != Guid.Empty
            && matrixVersionId != Guid.Empty && matchedRuleId != Guid.Empty
            && SecondPersonText.IsPerson(requester) && SecondPersonText.IsPerson(beneficiary) && SecondPersonText.IsPerson(policyMaker)
            && IsHash(entitlementContentHash) && IsHash(matrixContentHash)
            && (accountId is null) == (newAccount is not null) && accountId != Guid.Empty
            && (newAccount is null || (IsAccountType(newAccount.Type) && (newAccount.Name is null || IsAccountName(newAccount.Name))))
            && durationDays is null or (>= 1 and <= 3650)
            && evidence is { Length: > 0 and <= MaxEvidenceLength } && route is { Length: > 0 and <= MaxRouteLength };
        if (!valid)
        {
            throw new RequestRefusedException(RequestRefusal.InvalidRecord);
        }

        return new RequestItem
        {
            Id = id,
            Requester = requester,
            Beneficiary = beneficiary,
            ApplicationId = applicationId,
            EntitlementId = entitlementId,
            EntitlementVersionId = entitlementVersionId,
            EntitlementContentHash = entitlementContentHash,
            AccountId = accountId,
            NewAccountType = newAccount?.Type,
            NewAccountName = newAccount?.Name,
            DurationDays = durationDays,
            State = RequestItemState.Submitted,
            MatrixVersionId = matrixVersionId,
            MatrixContentHash = matrixContentHash,
            MatchedRuleId = matchedRuleId,
            PolicyMaker = policyMaker,
            Evidence = evidence,
            Route = route,
            SubmittedAtUtc = now,
            CorrelationId = correlationId,
        };
    }

    private static bool IsTraceId(string value) =>
        value is { Length: 32 } && value.All(char.IsAsciiHexDigitLower) && value.Any(c => c != '0');

    /// <summary>
    /// "Access for" (spec v3.2 §6.4): no expiry gives none and takes none; fixed days gives the entitlement's; requester chooses
    /// takes the requested days up to the maximum, or the default. Anything else is refused.
    /// </summary>
    public static int? Duration(string mode, int? days, int? defaultDays, int? maxDays, int? requested) => mode switch
    {
        "NoExpiry" when requested is null => null,
        "FixedDays" when requested is null && days is >= 1 => days,
        "RequesterChooses" when requested is null && defaultDays is >= 1 => defaultDays,
        "RequesterChooses" when requested is >= 1 && maxDays is { } max && requested <= max => requested,
        _ => throw new RequestRefusedException(RequestRefusal.InvalidDuration),
    };

    public static bool IsAccountType(string? type) => type is "Primary" or "Secondary" or "Service" or "Shared";

    /// <summary>The Account module's name shape (DL-089): letters, digits, dots, underscores, hyphens, starting with a letter or digit.</summary>
    public static bool IsAccountName(string? name) => name is { Length: >= 1 and <= 256 } && NamePattern().IsMatch(name);

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NamePattern();
}
