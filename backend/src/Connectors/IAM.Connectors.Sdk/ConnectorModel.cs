using System.Text.RegularExpressions;

namespace IAM.Connectors.Sdk;

/// <summary>
/// An object in the target system, by its type and the connector's own identifier (an AD distinguished name, say). The
/// SDK checks only shape and bounds; each connector checks its own identifier syntax as well.
/// </summary>
public sealed record ObjectRef(string ObjectType, string Id)
{
    public const int MaxIdLength = 512;

    public bool IsValid => ConnectorText.IsCode(ObjectType, 50) && ConnectorText.IsIdentifier(Id, MaxIdLength);

    /// <summary>The type only: an identifier can name a person.</summary>
    public override string ToString() => $"{nameof(ObjectRef)} {ObjectType}";
}

/// <summary>
/// Where a secret lives (a vault and an entry name), never the secret (CLAUDE.md "no password fields", DL-080). The
/// connector fetches it inside the customer's network; IAM never holds or sends the value.
/// </summary>
public sealed partial record SecretReference(string Vault, string Name)
{
    public bool IsValid => Vault is not null && Name is not null && VaultPattern().IsMatch(Vault) && NamePattern().IsMatch(Name);

    public override string ToString() => $"{nameof(SecretReference)} {Vault}";

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,49}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex VaultPattern();

    /// <summary>Path segments each start with a letter or digit: no '..', no '//', no leading '/'.</summary>
    [GeneratedRegex(@"^(?=.{1,200}\z)[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NamePattern();
}

/// <summary>
/// One change to make (DL-080). <see cref="OperationId"/> makes it safe to send again: the connector remembers what it did
/// for each id. <see cref="Attributes"/> are allowed only on create and update actions, and only the names the connector
/// declares for the object type; never a credential. A password is given only as <see cref="Secret"/>, a reference.
/// </summary>
public sealed record ConnectorOperation(
    Guid OperationId,
    string Action,
    ObjectRef Target,
    ObjectRef? Related,
    IReadOnlyDictionary<string, string?> Attributes,
    SecretReference? Secret)
{
    public const int MaxAttributes = 64;
    public const int MaxAttributeValueLength = 1024;

    /// <summary>The id and action only: targets and values can name people.</summary>
    public override string ToString() => $"{nameof(ConnectorOperation)} {OperationId} {Action}";
}

/// <summary>How an operation ended.</summary>
public enum OperationStatus
{
    /// <summary>The change was made.</summary>
    Applied = 1,

    /// <summary>The target was already as asked (read before write), or this operation id was already applied.</summary>
    AlreadyInState = 2,

    /// <summary>It can never succeed as given: fix the request, don't retry it.</summary>
    Rejected = 3,

    /// <summary>It failed for now (unavailable, throttled, timed out): retry later with the same operation id.</summary>
    Failed = 4,

    /// <summary>This connector can't do this action.</summary>
    Unsupported = 5,
}

/// <summary>The outcome of one operation: a status and a short code, never message text from the target system.</summary>
public sealed record OperationResult(Guid OperationId, OperationStatus Status, string? Code)
{
    public static OperationResult Of(ConnectorOperation operation, OperationStatus status, string? code = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new(operation.OperationId, status, code);
    }
}

/// <summary>Result codes (DL-080). Stored and never renamed.</summary>
public static class ResultCodes
{
    public const string UnknownAction = "UnknownAction";
    public const string NotSupported = "NotSupported";
    public const string ActionNotAllowed = "ActionNotAllowed";
    public const string InvalidOperation = "InvalidOperation";
    public const string AttributeNotAllowed = "AttributeNotAllowed";
    public const string CredentialInAttributes = "CredentialInAttributes";
    public const string SecretReferenceRequired = "SecretReferenceRequired";
    public const string SecretReferenceNotAllowed = "SecretReferenceNotAllowed";
    public const string OperationIdReused = "OperationIdReused";
    public const string TargetNotFound = "TargetNotFound";
    public const string RelatedNotFound = "RelatedNotFound";
    public const string AlreadyExists = "AlreadyExists";
    public const string ForbiddenAttribute = "ForbiddenAttribute";
    public const string SecretOutsideAllowlist = "SecretOutsideAllowlist";
    public const string ConnectorError = "ConnectorError";
    public const string InvalidResponse = "InvalidResponse";
    public const string TargetUnavailable = "TargetUnavailable";
    public const string Throttled = "Throttled";

    /// <summary>
    /// The SDK's own speed limit refused the operation before the connector was called (DL-104): nothing was written. Only the
    /// SDK sets it; a connector reporting it is reported as <see cref="ConnectorError"/>.
    /// </summary>
    public const string RateLimited = "RateLimited";
    public const string TimedOut = "TimedOut";
}

/// <summary>
/// What the connector reads of one object (DL-080): its state, the attributes it declares, its links (an account's
/// entitlements, an entitlement's members) and its owner. Never a credential.
/// </summary>
public sealed record ObjectSnapshot(
    ObjectRef Ref,
    bool? Enabled,
    bool? Locked,
    IReadOnlyDictionary<string, string?> Attributes,
    IReadOnlyList<ObjectRef> Links,
    ObjectRef? Owner)
{
    public override string ToString() => $"{nameof(ObjectSnapshot)} {Ref.ObjectType}";
}

/// <summary>Whether the target now matches what an operation asked for.</summary>
public sealed record VerificationResult(bool Matches, string? Code);

public enum HealthStatus
{
    Healthy = 1,
    Degraded = 2,
    Unavailable = 3,
}

/// <summary>A connector's health, and whether it may be used in production at all (CLAUDE.md rule 10, FR-REL-103).</summary>
public sealed record ConnectorHealth(HealthStatus Status, string? Code, bool ProductionReady);

/// <summary>One page of objects for discovery or reconciliation. <see cref="Continuation"/> is null on the last page.</summary>
public sealed record ObjectPage(IReadOnlyList<ObjectSnapshot> Items, string? Continuation);

/// <summary>An object type a connector works on, whether it can discover it, and the attribute names it reads and writes.</summary>
public sealed record ObjectTypeCapability(string ObjectType, bool Discoverable, IReadOnlyList<string> Attributes);

/// <summary>
/// What a connector type can do (spec 2.1: "each type declares what it can do, and which objects it can discover"). A
/// connector that isn't production-ready says so here and in its health, and the platform must not present it otherwise.
/// </summary>
public sealed record ConnectorCapabilities(
    string ConnectorType,
    bool ProductionReady,
    IReadOnlyList<string> Actions,
    IReadOnlyList<ObjectTypeCapability> ObjectTypes,
    int MaxOperationsPerSecond,
    int MaxPageSize,
    bool IdsIgnoreCase = false)
{
    public const int PageSizeLimit = 1000;

    /// <summary>How this connector's identifiers compare (DL-083b): a directory such as AD ignores case.</summary>
    public StringComparer IdComparer => IdsIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Object references compared the connector's way: type exactly, identifier by <see cref="IdComparer"/>.</summary>
    public IEqualityComparer<ObjectRef> RefComparer => IdsIgnoreCase ? ObjectRefComparer.IgnoreCase : ObjectRefComparer.Exact;

    /// <summary>Every action is in the catalog and its object types are declared; limits are in range.</summary>
    public bool IsValid =>
        ConnectorText.IsCode(ConnectorType, 50)
        && Actions.Count > 0
        && Actions.Distinct(StringComparer.Ordinal).Count() == Actions.Count
        && Actions.All(a => ActionCatalog.Find(a) is { } d && Declares(d.Target) && (d.Related is null || Declares(d.Related)))
        && ObjectTypes.All(t => ConnectorText.IsCode(t.ObjectType, 50) && t.Attributes.All(ConnectorText.IsAttributeName) && !t.Attributes.Any(ConnectorText.IsForbiddenAttribute))
        && MaxOperationsPerSecond is >= 1 and <= 10_000
        && MaxPageSize is >= 1 and <= PageSizeLimit;

    public bool Supports(string action) => Actions.Contains(action, StringComparer.Ordinal);

    public ObjectTypeCapability? TypeOf(string objectType) => ObjectTypes.FirstOrDefault(t => string.Equals(t.ObjectType, objectType, StringComparison.Ordinal));

    private bool Declares(string objectType) => TypeOf(objectType) is not null;
}

/// <summary>
/// How a connector instance is allowed to behave (spec 2.4, DL-080f, DL-083): destructive and credential actions start off
/// and are switched on one by one; an application can set a speed limit below the connector's own (spec 4.4); a password
/// change may use only entries under one vault path. The action list is copied when made, so it can't be changed later,
/// and every value is checked.
/// </summary>
public sealed record ConnectorSettings
{
    private readonly int? _maxOperationsPerSecond;
    private readonly string? _secretVault;
    private readonly string? _secretPathPrefix;

    public ConnectorSettings(IEnumerable<string> enabledGatedActions)
    {
        ArgumentNullException.ThrowIfNull(enabledGatedActions);
        var codes = enabledGatedActions.ToHashSet(StringComparer.Ordinal);
        if (codes.Any(c => ActionCatalog.Find(c) is not { Gated: true }))
        {
            throw new ArgumentException("Only destructive or credential catalog actions are switched on.", nameof(enabledGatedActions));
        }

        EnabledGatedActions = System.Collections.Frozen.FrozenSet.ToFrozenSet(codes, StringComparer.Ordinal);
    }

    public static ConnectorSettings Default { get; } = new([]);

    /// <summary>The destructive and credential actions switched on for this connector instance.</summary>
    public IReadOnlySet<string> EnabledGatedActions { get; }

    /// <summary>Operations per second for this application, if lower than the connector's declared limit (1 to 10,000).</summary>
    public int? MaxOperationsPerSecond
    {
        get => _maxOperationsPerSecond;
        init => _maxOperationsPerSecond = value is null or (>= 1 and <= 10_000)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "The speed limit must be 1 to 10,000 operations per second.");
    }

    /// <summary>The only vault a password change may read from.</summary>
    public string? SecretVault
    {
        get => _secretVault;
        init => _secretVault = value is null || new SecretReference(value, "x").IsValid
            ? value
            : throw new ArgumentException("Not a vault name.", nameof(value));
    }

    /// <summary>The only path, ending in '/', a password change's entry may sit under.</summary>
    public string? SecretPathPrefix
    {
        get => _secretPathPrefix;
        init => _secretPathPrefix = value is null || (value.EndsWith('/') && new SecretReference("v", value + "x").IsValid)
            ? value
            : throw new ArgumentException("Not a vault path ending in '/'.", nameof(value));
    }

    public static ConnectorSettings Allowing(params string[] gatedActions) => new(gatedActions);

    public bool Allows(ActionDefinition action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return !action.Gated || EnabledGatedActions.Contains(action.Code);
    }

    /// <summary>A reference inside the configured vault and path; nothing is allowed until both are set.</summary>
    public bool AllowsSecret(SecretReference secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return SecretVault is not null && SecretPathPrefix is not null
            && string.Equals(secret.Vault, SecretVault, StringComparison.Ordinal)
            && secret.Name.StartsWith(SecretPathPrefix, StringComparison.Ordinal);
    }
}

/// <summary>Compares object references: the type exactly, the identifier exactly or ignoring case.</summary>
public sealed class ObjectRefComparer : IEqualityComparer<ObjectRef>
{
    public static readonly ObjectRefComparer Exact = new(StringComparer.Ordinal);
    public static readonly ObjectRefComparer IgnoreCase = new(StringComparer.OrdinalIgnoreCase);

    private readonly StringComparer _ids;

    private ObjectRefComparer(StringComparer ids)
    {
        _ids = ids;
    }

    public bool Equals(ObjectRef? x, ObjectRef? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null && string.Equals(x.ObjectType, y.ObjectType, StringComparison.Ordinal) && _ids.Equals(x.Id, y.Id));

    public int GetHashCode(ObjectRef obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return HashCode.Combine(obj.ObjectType, _ids.GetHashCode(obj.Id));
    }
}

/// <summary>A failure worth retrying (the target is down, busy or slow). Connectors throw it; the pipeline reports Failed.</summary>
public sealed class ConnectorTransientException : Exception
{
    public ConnectorTransientException()
    {
    }

    public ConnectorTransientException(string code)
        : base(code)
    {
        Code = code;
    }

    public ConnectorTransientException(string code, Exception innerException)
        : base(code, innerException)
    {
        Code = code;
    }

    public string Code { get; } = ResultCodes.TargetUnavailable;
}

/// <summary>Text rules shared by the SDK's checks.</summary>
public static partial class ConnectorText
{
    public static bool IsCode(string? value, int maxLength) =>
        value is not null && value.Length <= maxLength && CodePattern().IsMatch(value);

    /// <summary>Visible text with no control, format or separator characters, no surrounding spaces, within the bound.</summary>
    public static bool IsIdentifier(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength
        && !char.IsWhiteSpace(value[0]) && !char.IsWhiteSpace(value[^1])
        && value.All(IsVisible);

    public static bool IsAttributeName(string? name) => name is not null && AttributeNamePattern().IsMatch(name);

    /// <summary>Attribute values: bounded, no control or format characters (line breaks included).</summary>
    public static bool IsAttributeValue(string? value) =>
        value is null || (value.Length <= ConnectorOperation.MaxAttributeValueLength && value.All(IsVisible));

    /// <summary>An attribute name that would carry a credential: never written or read as an attribute.</summary>
    public static bool LooksLikeCredential(string name) =>
        name is not null && CredentialPattern().IsMatch(name);

    /// <summary>
    /// Attributes no connector may declare, write or read back (DL-083c): those that hold keys or secrets the name pattern
    /// misses, and those that grant access or privilege without the gated entitlement actions (CLAUDE.md rule 11). A
    /// reviewed list; adding a directory type adds its names here.
    /// </summary>
    public static IReadOnlySet<string> ForbiddenAttributes { get; } = System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            // Keys and secrets.
            "userPKCS12", "msFVE-KeyPackage", "msFVE-RecoveryPassword", "msTPM-OwnerInformation", "msTPM-TpmInformationForComputer",
            "krbPrincipalKey", "msDS-KeyCredentialLink", "ms-Mcs-AdmPwd", "msLAPS-Password", "msLAPS-EncryptedPassword",
            "msLAPS-EncryptedDSRMPassword", "supplementalCredentials", "dBCSPwd", "ntPwdHistory", "lmPwdHistory", "unicodePwd",
            "userPassword", "msDS-ManagedPassword",
            // Access and privilege.
            "userAccountControl", "servicePrincipalName", "sIDHistory", "altSecurityIdentities", "userCertificate",
            "msDS-AllowedToActOnBehalfOfOtherIdentity", "msDS-AllowedToDelegateTo", "member", "memberOf", "primaryGroupID",
            "scriptPath", "profilePath", "homeDirectory", "adminCount", "nTSecurityDescriptor", "objectSid", "managedBy",
            "msDS-GroupMSAMembership", "msExchDelegateListLink", "gPLink", "groupType",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsForbiddenAttribute(string name) =>
        name is not null && (LooksLikeCredential(name) || ForbiddenAttributes.Contains(name));

    private static bool IsVisible(char c) =>
        !char.IsControl(c) && !char.IsSurrogate(c)
        && char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator);

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,63}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex AttributeNamePattern();

    [GeneratedRegex(@"(?i)(pass(word)?|pwd|secret|credential|token|unicodepwd|apikey|privatekey)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CredentialPattern();
}
