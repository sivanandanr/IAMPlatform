namespace IAM.Modules.Catalog.Contracts;

/// <summary>
/// An entitlement's active version as other modules see it (DL-087), with the version and content hash it came from (evidence).
/// Enum-like fields carry their stable names.
/// </summary>
public sealed record RequestableEntitlement(
    Guid EntitlementId,
    string Key,
    Guid ApplicationId,
    string ApplicationKey,
    Guid VersionId,
    int VersionNumber,
    string ContentHash,
    string Type,
    string TechnicalName,
    bool Privileged,
    string Risk,
    IReadOnlyList<string> AllowedAccountTypes,
    string AccessForMode,
    int? AccessForDays,
    int? AccessForDefaultDays,
    int? AccessForMaxDays)
{
    /// <summary>The ids only: logging this object must not expose content.</summary>
    public override string ToString() => $"{nameof(RequestableEntitlement)} {EntitlementId}";
}

/// <summary>Who owns an entitlement's active version (DL-087a): a person or a workgroup, and the escalation workgroup (DEC-002).</summary>
public sealed record EntitlementOwnership(Guid EntitlementId, int VersionNumber, string ContentHash, string OwnerKind, string OwnerId, string EscalationWorkgroup)
{
    public override string ToString() => $"{nameof(EntitlementOwnership)} {EntitlementId}";
}

/// <summary>
/// Read-only view of entitlements for other modules (ADR-0001). Only active versions under an active application count;
/// drafts and superseded versions are never returned.
/// </summary>
public interface IEntitlementDirectory
{
    /// <summary>The active version, or null if the entitlement doesn't exist, has no active version, or its application isn't active.</summary>
    Task<RequestableEntitlement?> FindRequestableAsync(Guid entitlementId, CancellationToken cancellationToken);

    /// <summary>The active version's owner and escalation workgroup, or null.</summary>
    Task<EntitlementOwnership?> FindOwnershipAsync(Guid entitlementId, CancellationToken cancellationToken);

    /// <summary>A usable (built-in or approved) application role type's id by code, or null.</summary>
    Task<Guid?> FindRoleTypeIdAsync(string code, CancellationToken cancellationToken);
}
