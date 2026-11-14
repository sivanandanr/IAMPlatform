namespace IAM.Modules.Organization.Contracts;

/// <summary>A workgroup's active members, as other modules see them (DL-068).</summary>
public sealed record WorkgroupMembership(string Key, int VersionNumber, IReadOnlyList<string> Members);

/// <summary>
/// Read-only view of IAM workgroups for other modules (ADR-0001: no cross-module table access). Catalog checks that a
/// Group holder names a workgroup; Workflow resolves a workgroup's members when a level opens (stage 4 slice 2).
/// </summary>
public interface IWorkgroupDirectory
{
    /// <summary>True if a workgroup with this key is registered (active or not yet active).</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    /// <summary>The active member list, or null if the workgroup doesn't exist or has none active yet.</summary>
    Task<WorkgroupMembership?> FindActiveAsync(string key, CancellationToken cancellationToken);
}
