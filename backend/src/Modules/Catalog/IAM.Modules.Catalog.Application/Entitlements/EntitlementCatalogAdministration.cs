using IAM.Modules.Catalog.Domain.Applications;
using IAM.Modules.Catalog.Domain.Entitlements;

namespace IAM.Modules.Catalog.Application.Entitlements;

/// <summary>An entitlement's governed content as the API receives it: every value as text, parsed here so Endpoints never touches domain types.</summary>
public sealed record EntitlementSettingsText(
    string? Type,
    string? TechnicalName,
    string? Description,
    string? OwnerKind,
    string? OwnerId,
    string? EscalationWorkgroup,
    bool? Privileged,
    string? Risk,
    IReadOnlyList<string?>? AllowedAccountClasses,
    string? AccessForMode,
    int? AccessForDays,
    int? AccessForDefaultDays,
    int? AccessForMaxDays);

public sealed record AccessForView(string Mode, int? Days, int? DefaultDays, int? MaxDays);

/// <summary>One entitlement version. <c>ConcurrencyVersion</c> is the token to send back when editing a draft.</summary>
public sealed record EntitlementVersionView(
    Guid VersionId,
    int VersionNumber,
    string State,
    string Type,
    string TechnicalName,
    string Description,
    string OwnerKind,
    string OwnerId,
    string EscalationWorkgroup,
    bool Privileged,
    string Risk,
    IReadOnlyList<string> AllowedAccountClasses,
    AccessForView AccessFor,
    string ContentHash,
    string? OwnerAcceptedBy,
    DateTimeOffset? OwnerAcceptedAtUtc,
    long ConcurrencyVersion);

/// <summary>An entitlement with its active version (if any), its draft (if any) and whether the draft still needs the owner to accept.</summary>
public sealed record EntitlementView(
    Guid Id,
    Guid ApplicationId,
    string ApplicationKey,
    string Key,
    string DisplayName,
    bool IsActive,
    EntitlementVersionView? Active,
    EntitlementVersionView? Draft,
    bool DraftNeedsAcceptance,
    long ConcurrencyVersion);

/// <summary>One line of an application's entitlement list.</summary>
public sealed record EntitlementSummary(Guid Id, string Key, string DisplayName, bool IsActive, bool HasDraft);

public sealed record EntitlementChangeRequestView(
    Guid Id,
    Guid EntitlementId,
    int VersionNumber,
    string ContentHash,
    string OwnerKind,
    string OwnerId,
    string? AcceptedBy,
    string Maker,
    string Reason,
    DateTimeOffset ProposedAtUtc,
    string State,
    string? Checker,
    string? DecisionReason,
    DateTimeOffset? DecidedAtUtc);

/// <summary>A request with the draft it would activate, if that draft is still the one named.</summary>
public sealed record EntitlementChangeReview(EntitlementChangeRequestView Request, EntitlementVersionView? Draft);

/// <summary>
/// Entitlement catalog administration (DL-084h, DL-087). Every write checks the caller's permission and is audited in the
/// same transaction, and every refusal is audited too. Activation and every later change of what an entitlement is, who
/// owns it or which accounts may hold it need the new owner's acceptance and a second person.
/// </summary>
public interface IEntitlementCatalogAdministration
{
    Task<EntitlementView> RegisterAsync(Guid applicationId, string key, string displayName, EntitlementSettingsText settings, CancellationToken cancellationToken);

    Task<EntitlementView> RenameAsync(Guid entitlementId, long expectedConcurrencyVersion, string displayName, CancellationToken cancellationToken);

    Task<EntitlementView> UpdateDraftAsync(Guid entitlementId, long expectedConcurrencyVersion, EntitlementSettingsText settings, CancellationToken cancellationToken);

    Task<EntitlementView> StartNewDraftAsync(Guid entitlementId, EntitlementSettingsText settings, CancellationToken cancellationToken);

    /// <summary>The named owner (or a member of the owning workgroup) accepts the draft with this content hash.</summary>
    Task<EntitlementView> AcceptOwnershipAsync(Guid entitlementId, string contentHash, CancellationToken cancellationToken);

    Task<EntitlementView?> FindAsync(string applicationKey, string key, CancellationToken cancellationToken);

    Task<IReadOnlyList<EntitlementSummary>?> ListAsync(string applicationKey, CancellationToken cancellationToken);

    Task<EntitlementChangeRequestView> ProposeAsync(Guid entitlementId, string reason, CancellationToken cancellationToken);

    /// <summary>The approver names the content hash they reviewed; anything other than the proposed content is refused.</summary>
    Task<EntitlementChangeRequestView> ApproveAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken);

    Task<EntitlementChangeRequestView> RejectAsync(Guid requestId, string reason, CancellationToken cancellationToken);

    Task<EntitlementChangeRequestView> WithdrawAsync(Guid requestId, string? reason, CancellationToken cancellationToken);

    Task<EntitlementChangeReview?> ReviewRequestAsync(Guid requestId, CancellationToken cancellationToken);
}

public static class EntitlementApi
{
    /// <summary>
    /// Builds settings from API text. Enum values must be the exact names (no numbers, no other case); anything else is a
    /// field error. The domain checks every rule again.
    /// </summary>
    public static EntitlementSettings ToSettings(EntitlementSettingsText? text)
    {
        if (text is null)
        {
            throw new ApplicationRejectedException("Settings", "are required");
        }

        if (text.AllowedAccountClasses is null || text.AllowedAccountClasses.Count > 4 || text.AllowedAccountClasses.Any(c => c is null))
        {
            throw new ApplicationRejectedException("AllowedAccountClasses", "must list at most four account types");
        }

        return new EntitlementSettings(
            Required<EntitlementType>(text.Type, "Type"),
            text.TechnicalName ?? string.Empty,
            text.Description ?? string.Empty,
            Required<RoleHolderKind>(text.OwnerKind, "OwnerKind"),
            text.OwnerId ?? string.Empty,
            text.EscalationWorkgroup ?? string.Empty,
            text.Privileged ?? throw new ApplicationRejectedException("Privileged", "is required"),
            Required<EntitlementRisk>(text.Risk, "Risk"),
            [.. text.AllowedAccountClasses.Select(c => Required<AccountClass>(c, "AllowedAccountClasses"))],
            new AccessFor(Required<AccessForMode>(text.AccessForMode, "AccessForMode"), text.AccessForDays, text.AccessForDefaultDays, text.AccessForMaxDays));
    }

    private static TEnum Required<TEnum>(string? text, string field)
        where TEnum : struct, Enum =>
        text is not null && Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal)
            ? Enum.Parse<TEnum>(text)
            : throw new ApplicationRejectedException(field, $"must be one of {string.Join(", ", Enum.GetNames<TEnum>())}");
}
