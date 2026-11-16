using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using IAM.BuildingBlocks.Abstractions.SecondPerson;
using IAM.Modules.Catalog.Domain.Applications;

namespace IAM.Modules.Catalog.Domain.Entitlements;

/// <summary>The kinds of entitlement the spec names (v3.2 §6.3). Stored by name.</summary>
public enum EntitlementType
{
    SecurityGroup = 1,
    DistributionList = 2,
    SharedMailbox = 3,
}

/// <summary>
/// An entitlement's risk (spec v3.2 §6.3 "risk"; §7 uses "High risk or above"). The four levels are a stage 8 default
/// awaiting the BA (DL-088).
/// </summary>
public enum EntitlementRisk
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

/// <summary>
/// The account types an entitlement may be placed on (spec v3.2 §2.5, §3.1, DL-084f). "Primary" is a person's normal account
/// (DL-084: "User" is an identity category, not an account type). Non-human identities are not an account type (DL-085).
/// </summary>
public enum AccountClass
{
    Primary = 1,
    Secondary = 2,
    Service = 3,
    Shared = 4,
}

/// <summary>How long access to an entitlement lasts (spec v3.2 §6.4).</summary>
public enum AccessForMode
{
    NoExpiry = 1,
    FixedDays = 2,
    RequesterChooses = 3,
}

/// <summary>
/// "Access for" (spec v3.2 §6.4): no expiry; a fixed number of days; or the requester chooses up to <see cref="MaxDays"/>,
/// starting from <see cref="DefaultDays"/>. Privileged access always has an end date.
/// </summary>
public sealed record AccessFor(AccessForMode Mode, int? Days, int? DefaultDays, int? MaxDays)
{
    public static AccessFor NoExpiry { get; } = new(AccessForMode.NoExpiry, null, null, null);

    public static AccessFor Fixed(int days) => new(AccessForMode.FixedDays, days, null, null);

    public static AccessFor RequesterChooses(int defaultDays, int maxDays) => new(AccessForMode.RequesterChooses, null, defaultDays, maxDays);
}

/// <summary>
/// The governed content of an entitlement version (DL-084h, DL-087): what it is in the target, who owns it, how risky it
/// is, which accounts may hold it and for how long. Changing any of it needs the owner's acceptance (when the owner changes)
/// and a second person.
/// </summary>
public sealed record EntitlementSettings(
    EntitlementType Type,
    string TechnicalName,
    string Description,
    RoleHolderKind OwnerKind,
    string OwnerId,
    string EscalationWorkgroup,
    bool Privileged,
    EntitlementRisk Risk,
    IReadOnlyList<AccountClass> AllowedAccountClasses,
    AccessFor AccessFor);

/// <summary>Input rules and the content hash of entitlement versions.</summary>
public static class EntitlementRules
{
    public const int TechnicalNameMaxLength = 256;
    public const int DescriptionMaxLength = 2000;
    public const int MaxDays = 3650;

    /// <summary>Part of the hashed text, so the layout can change later without ambiguity.</summary>
    public const string ContentHashLayout = "iam.catalog.entitlement.v1";

    /// <summary>
    /// The canonical stored form of an account class set: the names in enum order, comma separated. The database accepts
    /// only these spellings.
    /// </summary>
    public static string ToStored(IEnumerable<AccountClass> classes) =>
        string.Join(',', classes.Distinct().Order().Select(c => c.ToString()));

    public static IReadOnlyList<AccountClass> FromStored(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
        }

        var classes = new List<AccountClass>();
        foreach (var part in stored.Split(','))
        {
            if (!Enum.GetNames<AccountClass>().Contains(part, StringComparer.Ordinal))
            {
                throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
            }

            classes.Add(Enum.Parse<AccountClass>(part));
        }

        return string.Equals(ToStored(classes), stored, StringComparison.Ordinal) ? classes : throw new ApplicationRefusedException(ApplicationRefusal.InvalidRecord);
    }

    /// <summary>
    /// Every rule an entitlement version must meet. Privileged access never goes on a Primary account and always has an end
    /// date; a Secondary account is only for privileged access (spec v3.2 §2.5, §3.1, §6.4).
    /// </summary>
    public static void Validate(EntitlementSettings? settings)
    {
        if (settings is null)
        {
            throw new ApplicationRejectedException("Settings", "are required");
        }

        if (!Enum.IsDefined(settings.Type))
        {
            throw new ApplicationRejectedException("Type", "must be SecurityGroup, DistributionList or SharedMailbox");
        }

        if (!SecondPersonText.IsText(settings.TechnicalName, TechnicalNameMaxLength) || settings.TechnicalName.Trim().Length != settings.TechnicalName.Length)
        {
            throw new ApplicationRejectedException("TechnicalName", $"must be visible text of at most {TechnicalNameMaxLength} characters, without leading or trailing spaces");
        }

        if (!SecondPersonText.IsText(settings.Description, DescriptionMaxLength))
        {
            throw new ApplicationRejectedException("Description", $"must be visible text of at most {DescriptionMaxLength} characters");
        }

        if (!Enum.IsDefined(settings.OwnerKind)
            || (settings.OwnerKind == RoleHolderKind.Person && !SecondPersonText.IsPerson(settings.OwnerId))
            || (settings.OwnerKind == RoleHolderKind.Group && !IsKey(settings.OwnerId)))
        {
            throw new ApplicationRejectedException("Owner", "must be one person's id or a workgroup's key");
        }

        if (!IsKey(settings.EscalationWorkgroup))
        {
            throw new ApplicationRejectedException("EscalationWorkgroup", "must be a workgroup's key");
        }

        if (!Enum.IsDefined(settings.Risk))
        {
            throw new ApplicationRejectedException("Risk", "must be Low, Medium, High or Critical");
        }

        var classes = settings.AllowedAccountClasses;
        if (classes is null || classes.Count == 0 || classes.Count > 4 || classes.Any(c => !Enum.IsDefined(c)) || classes.Distinct().Count() != classes.Count)
        {
            throw new ApplicationRejectedException("AllowedAccountClasses", "must name each of Primary, Secondary, Service or Shared at most once, and at least one");
        }

        if (settings.Privileged && classes.Contains(AccountClass.Primary))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.PrivilegedOnPrimary);
        }

        if (!settings.Privileged && classes.Contains(AccountClass.Secondary))
        {
            throw new ApplicationRefusedException(ApplicationRefusal.SecondaryOnlyForPrivileged);
        }

        ValidateAccessFor(settings.AccessFor, settings.Privileged);
    }

    /// <summary>
    /// Lower-case hex SHA-256 over the UTF-8 compact JSON array <c>[layout, entitlementKey, versionNumber, type, technicalName,
    /// description, ownerKind, ownerId, escalationWorkgroup, privileged, risk, accountClasses, accessMode, days, defaultDays,
    /// maxDays]</c>, enum values by name, the account classes in their stored form and absent numbers as nulls.
    /// </summary>
    public static string ComputeContentHash(string entitlementKey, int versionNumber, EntitlementSettings settings)
    {
        ArgumentNullException.ThrowIfNull(entitlementKey);
        ArgumentNullException.ThrowIfNull(settings);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false }))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(ContentHashLayout);
            writer.WriteStringValue(entitlementKey);
            writer.WriteNumberValue(versionNumber);
            writer.WriteStringValue(settings.Type.ToString());
            writer.WriteStringValue(settings.TechnicalName);
            writer.WriteStringValue(settings.Description);
            writer.WriteStringValue(settings.OwnerKind.ToString());
            writer.WriteStringValue(settings.OwnerId);
            writer.WriteStringValue(settings.EscalationWorkgroup);
            writer.WriteBooleanValue(settings.Privileged);
            writer.WriteStringValue(settings.Risk.ToString());
            writer.WriteStringValue(ToStored(settings.AllowedAccountClasses));
            writer.WriteStringValue(settings.AccessFor.Mode.ToString());
            WriteNumber(writer, settings.AccessFor.Days);
            WriteNumber(writer, settings.AccessFor.DefaultDays);
            WriteNumber(writer, settings.AccessFor.MaxDays);
            writer.WriteEndArray();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>The owner as one text, for audit records and the request row: <c>Person:id</c> or <c>Group:key</c>.</summary>
    public static string OwnerText(RoleHolderKind kind, string id) => string.Create(CultureInfo.InvariantCulture, $"{kind}:{id}");

    public static bool IsKey(string? key)
    {
        try
        {
            CatalogRules.ValidateKey(key, "Key");
            return true;
        }
        catch (ApplicationRejectedException)
        {
            return false;
        }
    }

    private static void ValidateAccessFor(AccessFor? accessFor, bool privileged)
    {
        static bool Days(int? days) => days is >= 1 and <= MaxDays;
        var valid = accessFor switch
        {
            { Mode: AccessForMode.NoExpiry, Days: null, DefaultDays: null, MaxDays: null } => true,
            { Mode: AccessForMode.FixedDays, DefaultDays: null, MaxDays: null } a => Days(a.Days),
            { Mode: AccessForMode.RequesterChooses, Days: null } a => Days(a.DefaultDays) && Days(a.MaxDays) && a.DefaultDays <= a.MaxDays,
            _ => false,
        };
        if (!valid)
        {
            throw new ApplicationRejectedException("AccessFor", $"must be NoExpiry, FixedDays with 1-{MaxDays} days, or RequesterChooses with a default no larger than the maximum of 1-{MaxDays} days");
        }

        if (privileged && accessFor!.Mode == AccessForMode.NoExpiry)
        {
            throw new ApplicationRefusedException(ApplicationRefusal.PrivilegedNeedsEndDate);
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
