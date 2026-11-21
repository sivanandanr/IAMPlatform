using IAM.Modules.Catalog.Contracts;
using IAM.Modules.Catalog.Domain.Applications;
using IAM.Modules.Catalog.Domain.Entitlements;
using IAM.Modules.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Catalog.Infrastructure.Entitlements;

/// <summary>Entitlements for other modules (DL-087, DL-091). Reads only active versions; no permission check, as for other module contracts.</summary>
public sealed class EntitlementDirectory(CatalogDbContext db) : IEntitlementDirectory
{
    public async Task<RequestableEntitlement?> FindRequestableAsync(Guid entitlementId, CancellationToken cancellationToken)
    {
        var found = await ActiveAsync(entitlementId, cancellationToken).ConfigureAwait(false);
        if (found is not { } f)
        {
            return null;
        }

        var applicationKey = await db.Applications.AsNoTracking()
            .Where(a => a.Id == f.Entitlement.ApplicationId && a.Versions.Any(v => v.State == SettingsVersionState.Active))
            .Select(a => a.Key)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (applicationKey is null)
        {
            return null;
        }

        // A stored version that breaks the rules is never offered (ToSettings refuses it as damaged).
        var settings = f.Version.ToSettings();
        return new RequestableEntitlement(
            f.Entitlement.Id,
            f.Entitlement.Key,
            f.Entitlement.ApplicationId,
            applicationKey,
            f.Version.Id,
            f.Version.VersionNumber,
            f.Version.ContentHash,
            settings.Type.ToString(),
            settings.TechnicalName,
            settings.Privileged,
            settings.Risk.ToString(),
            [.. settings.AllowedAccountClasses.Select(c => c.ToString())],
            settings.AccessFor.Mode.ToString(),
            settings.AccessFor.Days,
            settings.AccessFor.DefaultDays,
            settings.AccessFor.MaxDays);
    }

    public async Task<EntitlementOwnership?> FindOwnershipAsync(Guid entitlementId, CancellationToken cancellationToken)
    {
        var found = await ActiveAsync(entitlementId, cancellationToken).ConfigureAwait(false);
        return found is { } f
            ? new EntitlementOwnership(f.Entitlement.Id, f.Version.VersionNumber, f.Version.ContentHash, f.Version.OwnerKind.ToString(), f.Version.OwnerId, f.Version.EscalationWorkgroup)
            : null;
    }

    public async Task<Guid?> FindRoleTypeIdAsync(string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        return await db.RoleTypes.AsNoTracking()
            .Where(t => t.Code == code && (t.State == RoleTypeState.BuiltIn || t.State == RoleTypeState.Approved))
            .Select(t => (Guid?)t.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(Entitlement Entitlement, EntitlementVersion Version)?> ActiveAsync(Guid entitlementId, CancellationToken cancellationToken)
    {
        var entitlement = await db.Entitlements.AsNoTracking()
            .Include(e => e.Versions.Where(v => v.State == SettingsVersionState.Active))
            .SingleOrDefaultAsync(e => e.Id == entitlementId, cancellationToken)
            .ConfigureAwait(false);
        return entitlement?.CurrentActive is { } active ? (entitlement, active) : null;
    }
}
