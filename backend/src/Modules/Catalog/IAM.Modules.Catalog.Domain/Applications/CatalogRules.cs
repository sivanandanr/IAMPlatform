using System.Text.RegularExpressions;
using IAM.BuildingBlocks.Abstractions.SecondPerson;

namespace IAM.Modules.Catalog.Domain.Applications;

/// <summary>Where a settings version of an application stands (DL-066). Only an approved request makes a draft active.</summary>
public enum SettingsVersionState
{
    Draft = 1,
    Active = 2,
    Superseded = 3,
}

/// <summary>Whether an application needs a role filled (AR-2.2). An empty required role blocks activation (AR-2.3, DL-052).</summary>
public enum RoleRequirement
{
    Optional = 1,
    Required = 2,
}

/// <summary>
/// Who holds an application role (AR-2.2, DL-066a): a person (their sign-in id) or a directory group (its opaque id). A group
/// id is stored only; nothing resolves its members or trusts it until approver resolution (stage 4).
/// </summary>
public enum RoleHolderKind
{
    Person = 1,
    Group = 2,
}

/// <summary>
/// Where an application role type stands. Built-in types come with the product; a deployment's own type is proposed and
/// usable only once a second person approves it (DL-066c).
/// </summary>
public enum RoleTypeState
{
    BuiltIn = 1,
    Proposed = 2,
    Approved = 3,
    Rejected = 4,
    Withdrawn = 5,
}

/// <summary>Why a catalog change was refused. Codes only, never people or content.</summary>
public enum ApplicationRefusal
{
    NotAllowed = 1,
    NoDraft = 2,
    DraftChanged = 3,
    MakerCannotDecide = 4,
    OwnerCannotDecide = 5,
    NotProposed = 6,
    NotMaker = 7,
    InvalidReason = 8,
    InvalidRecord = 9,
    AlreadyPending = 10,
    DraftExists = 11,
    NothingActive = 12,
    MakerNoLongerAllowed = 13,
    RequiredRoleEmpty = 14,
    RoleTypeNotUsable = 15,
    RoleTypeExists = 16,
}

/// <summary>A catalog change was refused by a rule (see <see cref="ApplicationRefusal"/>).</summary>
public sealed class ApplicationRefusedException : Exception
{
    public ApplicationRefusedException()
    {
    }

    public ApplicationRefusedException(string message)
        : base(message)
    {
    }

    public ApplicationRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ApplicationRefusedException(ApplicationRefusal refusal, Exception? innerException = null)
        : base($"Catalog change refused: {refusal}.", innerException)
    {
        Refusal = refusal;
    }

    public ApplicationRefusal? Refusal { get; }
}

/// <summary>Catalog input was invalid. The message names the field and rule only, never the value.</summary>
public sealed class ApplicationRejectedException : Exception
{
    public ApplicationRejectedException()
    {
    }

    public ApplicationRejectedException(string message)
        : base(message)
    {
    }

    public ApplicationRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ApplicationRejectedException(string field, string rule, Exception? innerException = null)
        : base($"Catalog input rejected: {field} {rule}.", innerException)
    {
        Field = field;
    }

    public string? Field { get; }
}

/// <summary>One application role in a settings version: which type, whether it is required, and who holds it (if anyone).</summary>
public sealed record ApplicationRoleSetting(Guid RoleTypeId, RoleRequirement Requirement, RoleHolderKind? HolderKind, string? HolderId);

/// <summary>
/// The governed settings of an application (DL-066b): its single required owner (AR-2.4, a person) and its application roles.
/// Changing these on an active application needs a second person.
/// </summary>
public sealed record ApplicationSettings(string Owner, IReadOnlyList<ApplicationRoleSetting> Roles);

/// <summary>Input rules shared by applications and role types.</summary>
public static partial class CatalogRules
{
    public const int KeyMaxLength = 64;
    public const int DisplayNameMaxLength = 200;
    public const int RoleTypeDisplayNameMaxLength = 100;
    public const int MaxRoles = 32;
    public const int HashLength = 64;

    /// <summary>A key or code: 2-64 lower-case letters, digits or underscores, starting with a letter. Never changes once stored.</summary>
    public static void ValidateKey(string? key, string field)
    {
        if (key is null || !KeyPattern().IsMatch(key))
        {
            throw new ApplicationRejectedException(field, "must be 2-64 lower-case letters, digits or underscores, starting with a letter");
        }
    }

    public static void ValidateDisplayName(string? name, int maxLength, string field)
    {
        if (!SecondPersonText.IsText(name, maxLength))
        {
            throw new ApplicationRejectedException(field, $"must be visible text of at most {maxLength} characters");
        }
    }

    /// <summary>
    /// The owner is one person (AR-2.4, CLAUDE.md rule 5: no assumed backup). Each role type appears at most once; a holder
    /// has both a kind and an id, or neither (an empty role).
    /// </summary>
    public static void ValidateSettings(ApplicationSettings? settings)
    {
        if (settings is null)
        {
            throw new ApplicationRejectedException("Settings", "are required");
        }

        if (!SecondPersonText.IsPerson(settings.Owner))
        {
            throw new ApplicationRejectedException("Owner", "must be one person's id");
        }

        if (settings.Roles is null || settings.Roles.Count > MaxRoles)
        {
            throw new ApplicationRejectedException("Roles", $"must be a list of at most {MaxRoles} roles");
        }

        var seen = new HashSet<Guid>();
        foreach (var role in settings.Roles)
        {
            if (role is null || role.RoleTypeId == Guid.Empty || !seen.Add(role.RoleTypeId))
            {
                throw new ApplicationRejectedException("Roles", "must name each role type once");
            }

            if (!Enum.IsDefined(role.Requirement))
            {
                throw new ApplicationRejectedException("Requirement", "must be Optional or Required");
            }

            var hasKind = role.HolderKind is not null;
            if (hasKind != (role.HolderId is not null)
                || (role.HolderKind is { } kind && !Enum.IsDefined(kind))
                || (role.HolderId is not null && !SecondPersonText.IsPerson(role.HolderId)))
            {
                throw new ApplicationRejectedException("Holder", "must be a person or group with its id, or empty");
            }
        }
    }

    public static bool IsHash(string? hash) =>
        hash is { Length: HashLength } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex KeyPattern();
}
