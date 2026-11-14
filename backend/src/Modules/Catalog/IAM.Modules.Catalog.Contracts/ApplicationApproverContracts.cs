namespace IAM.Modules.Catalog.Contracts;

/// <summary>Whether an application role must be filled (AR-2.2). Same values as the catalog's own.</summary>
public enum ApplicationRoleRequirement
{
    Optional = 1,
    Required = 2,
}

/// <summary>Who holds an application role: a person's sign-in id, or an IAM workgroup's key (DL-068b).</summary>
public enum ApplicationRoleHolderKind
{
    Person = 1,
    Group = 2,
}

/// <summary>One role in an application's active settings. An empty role has no holder.</summary>
public sealed record ApplicationRoleHolding(Guid RoleTypeId, ApplicationRoleRequirement Requirement, ApplicationRoleHolderKind? HolderKind, string? HolderId);

/// <summary>An application's active owner and roles, with the settings version and content hash they came from (evidence, DL-069e).</summary>
public sealed record ApplicationApprovers(Guid ApplicationId, int VersionNumber, string ContentHash, string Owner, IReadOnlyList<ApplicationRoleHolding> Roles)
{
    /// <summary>The application id only: logging this object must not expose a person.</summary>
    public override string ToString() => $"{nameof(ApplicationApprovers)} {ApplicationId}";
}

/// <summary>
/// Read-only view of who an application's active settings name, for approver resolution (DL-069). Only active settings
/// count: a draft or a superseded version is never used (ADR-0001: no cross-module table access).
/// </summary>
public interface IApplicationApproverDirectory
{
    /// <summary>The active settings' owner and roles, or null if the application doesn't exist or has no active settings.</summary>
    Task<ApplicationApprovers?> FindActiveAsync(Guid applicationId, CancellationToken cancellationToken);
}
