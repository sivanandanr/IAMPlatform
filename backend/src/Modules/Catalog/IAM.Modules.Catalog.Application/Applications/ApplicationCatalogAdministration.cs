using IAM.Modules.Catalog.Domain.Applications;

namespace IAM.Modules.Catalog.Application.Applications;

/// <summary>One application role as a caller gives it: the role type by code, and the holder if any.</summary>
public sealed record ApplicationRoleInput(string RoleTypeCode, RoleRequirement Requirement, RoleHolderKind? HolderKind, string? HolderId);

/// <summary>An application's governed settings as a caller gives them.</summary>
public sealed record ApplicationSettingsInput(string Owner, IReadOnlyList<ApplicationRoleInput> Roles);

public sealed record RegisterApplicationRequest(string Key, string DisplayName, ApplicationSettingsInput Settings);

public sealed record ApplicationRoleView(string RoleTypeCode, string Requirement, string? HolderKind, string? HolderId);

/// <summary>One settings version. <c>ConcurrencyVersion</c> is the token to send back when editing a draft.</summary>
public sealed record ApplicationSettingsView(
    Guid VersionId,
    int VersionNumber,
    string State,
    string Owner,
    IReadOnlyList<ApplicationRoleView> Roles,
    string ContentHash,
    long ConcurrencyVersion);

/// <summary>An application with its active settings (if any) and its draft (if any).</summary>
public sealed record ApplicationView(
    Guid Id,
    string Key,
    string DisplayName,
    bool IsActive,
    ApplicationSettingsView? Active,
    ApplicationSettingsView? Draft,
    long ConcurrencyVersion);

public sealed record ApplicationSettingsRequestView(
    Guid Id,
    Guid ApplicationId,
    int VersionNumber,
    string ContentHash,
    string ProposedOwner,
    string Maker,
    string Reason,
    DateTimeOffset ProposedAtUtc,
    string State,
    string? Checker,
    string? DecisionReason,
    DateTimeOffset? DecidedAtUtc);

/// <summary>A request with the draft it would activate, if that draft is still the one named.</summary>
public sealed record ApplicationSettingsReview(ApplicationSettingsRequestView Request, ApplicationSettingsView? Draft);

public sealed record ApplicationRoleTypeView(
    Guid Id,
    string Code,
    string DisplayName,
    string State,
    string? Maker,
    string? Reason,
    string? Checker,
    string? DecisionReason,
    DateTimeOffset? DecidedAtUtc);

/// <summary>
/// Catalog administration (DL-066). Every write checks the caller's permission, is audited in the same transaction, and
/// every refusal is audited too. Activation and owner or role changes on an active application need a second person.
/// </summary>
public interface IApplicationCatalogAdministration
{
    Task<ApplicationView> RegisterAsync(RegisterApplicationRequest request, CancellationToken cancellationToken);

    Task<ApplicationView> RenameAsync(Guid applicationId, long expectedConcurrencyVersion, string displayName, CancellationToken cancellationToken);

    Task<ApplicationView> UpdateDraftAsync(Guid applicationId, long expectedConcurrencyVersion, ApplicationSettingsInput settings, CancellationToken cancellationToken);

    Task<ApplicationView> StartNewDraftAsync(Guid applicationId, ApplicationSettingsInput settings, CancellationToken cancellationToken);

    Task<ApplicationView?> FindAsync(string key, CancellationToken cancellationToken);

    Task<ApplicationSettingsRequestView> ProposeAsync(Guid applicationId, string reason, CancellationToken cancellationToken);

    /// <summary>The approver names the content hash they reviewed; anything other than the proposed content is refused.</summary>
    Task<ApplicationSettingsRequestView> ApproveAsync(Guid requestId, string reviewedContentHash, string? reason, CancellationToken cancellationToken);

    Task<ApplicationSettingsRequestView> RejectAsync(Guid requestId, string reason, CancellationToken cancellationToken);

    Task<ApplicationSettingsRequestView> WithdrawAsync(Guid requestId, string? reason, CancellationToken cancellationToken);

    Task<ApplicationSettingsReview?> ReviewRequestAsync(Guid requestId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApplicationRoleTypeView>> ListRoleTypesAsync(CancellationToken cancellationToken);

    Task<ApplicationRoleTypeView> ProposeRoleTypeAsync(string code, string displayName, string reason, CancellationToken cancellationToken);

    Task<ApplicationRoleTypeView> ApproveRoleTypeAsync(Guid roleTypeId, string? reason, CancellationToken cancellationToken);

    Task<ApplicationRoleTypeView> RejectRoleTypeAsync(Guid roleTypeId, string reason, CancellationToken cancellationToken);

    Task<ApplicationRoleTypeView> WithdrawRoleTypeAsync(Guid roleTypeId, string? reason, CancellationToken cancellationToken);
}

public sealed class ApplicationNotFoundException : Exception
{
    public ApplicationNotFoundException()
        : base("The application, request or role type was not found.")
    {
    }

    public ApplicationNotFoundException(string message)
        : base(message)
    {
    }

    public ApplicationNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Someone else changed the application, request or role type first; reload and try again.</summary>
public sealed class ApplicationConflictException : Exception
{
    public ApplicationConflictException()
        : base("The application was changed by someone else; reload it and try again.")
    {
    }

    public ApplicationConflictException(string message)
        : base(message)
    {
    }

    public ApplicationConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
