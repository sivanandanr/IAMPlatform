using IAM.Modules.Catalog.Domain.Applications;

namespace IAM.Modules.Catalog.Application.Applications;

/// <summary>One application role as the API receives it: every value as text, parsed here so Endpoints never touches domain types.</summary>
public sealed record ApplicationRoleText(string? RoleTypeCode, string? Requirement, string? HolderKind, string? HolderId);

/// <summary>How a caller-facing layer should treat a catalog failure, without naming domain types.</summary>
public enum CatalogFailureKind
{
    None = 0,

    /// <summary>A field breaks a rule; <c>field</c> names it and the message describes the rule, never the value.</summary>
    InvalidField = 1,

    /// <summary>Who the caller is or what they hold rules it out (not allowed, own request, the proposed owner, no longer allowed).</summary>
    Forbidden = 2,

    /// <summary>The application, request or role type is not in a state that allows this.</summary>
    WrongState = 3,

    /// <summary>The request breaks a catalog rule (a required role is empty, a role type isn't usable, a missing reason).</summary>
    Invalid = 4,

    /// <summary>Someone else changed the same thing first.</summary>
    Conflict = 5,

    NotFound = 6,

    /// <summary>A stored record failed its checks: fail closed.</summary>
    Unavailable = 7,
}

public static class CatalogApi
{
    /// <summary>
    /// Builds settings from API text. Enum values must be the exact names (no numbers, no other case), so a request can't
    /// slip an undefined value past the rules; anything else is a field error.
    /// </summary>
    public static ApplicationSettingsInput ToSettings(string? owner, IReadOnlyList<ApplicationRoleText?>? roles)
    {
        if (roles is null)
        {
            throw new ApplicationRejectedException("Roles", "are required (an empty list for none)");
        }

        var parsed = new List<ApplicationRoleInput>(roles.Count);
        foreach (var role in roles)
        {
            if (role is null)
            {
                throw new ApplicationRejectedException("Roles", "must not contain empty entries");
            }

            var requirement = Parse<RoleRequirement>(role.Requirement, "Requirement")
                ?? throw new ApplicationRejectedException("Requirement", "must be Optional or Required");
            var kind = Parse<RoleHolderKind>(role.HolderKind, "HolderKind");
            parsed.Add(new ApplicationRoleInput(role.RoleTypeCode ?? string.Empty, requirement, kind, role.HolderId));
        }

        return new ApplicationSettingsInput(owner ?? string.Empty, parsed);
    }

    /// <summary>Classifies a catalog failure. <paramref name="code"/> is an enum name and <paramref name="field"/> a field name, never data.</summary>
    public static CatalogFailureKind Classify(Exception exception, out string? code, out string? field, out string? rule)
    {
        code = null;
        field = null;
        rule = null;
        switch (exception)
        {
            case ApplicationRejectedException rejected:
                field = rejected.Field;
                rule = rejected.Message;
                return CatalogFailureKind.InvalidField;
            case ApplicationConflictException:
                return CatalogFailureKind.Conflict;
            case ApplicationNotFoundException:
                return CatalogFailureKind.NotFound;
            case ApplicationRefusedException refused:
                code = refused.Refusal?.ToString();
                return refused.Refusal switch
                {
                    ApplicationRefusal.NotAllowed or ApplicationRefusal.MakerCannotDecide or ApplicationRefusal.OwnerCannotDecide
                        or ApplicationRefusal.HolderCannotDecide or ApplicationRefusal.NotMaker or ApplicationRefusal.MakerNoLongerAllowed => CatalogFailureKind.Forbidden,
                    ApplicationRefusal.NotProposed or ApplicationRefusal.AlreadyPending or ApplicationRefusal.DraftExists
                        or ApplicationRefusal.NoDraft or ApplicationRefusal.DraftChanged
                        or ApplicationRefusal.RoleTypeExists => CatalogFailureKind.WrongState,
                    ApplicationRefusal.InvalidRecord or null => CatalogFailureKind.Unavailable,
                    _ => CatalogFailureKind.Invalid,
                };
            default:
                return CatalogFailureKind.None;
        }
    }

    private static TEnum? Parse<TEnum>(string? text, string field)
        where TEnum : struct, Enum
    {
        if (text is null)
        {
            return null;
        }

        return Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal)
            ? Enum.Parse<TEnum>(text)
            : throw new ApplicationRejectedException(field, $"must be one of {string.Join(", ", Enum.GetNames<TEnum>())}");
    }
}
