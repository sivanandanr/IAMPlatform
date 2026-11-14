using IAM.Modules.Catalog.Contracts;
using IAM.Modules.Catalog.Domain.Applications;
using IAM.Modules.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAM.Modules.Catalog.Infrastructure.Applications;

/// <summary>Implements <see cref="IApplicationApproverDirectory"/>: loads only the active settings version, without tracking.</summary>
public sealed class ApplicationApproverDirectory(CatalogDbContext db) : IApplicationApproverDirectory
{
    public async Task<ApplicationApprovers?> FindActiveAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        if (applicationId == Guid.Empty)
        {
            return null;
        }

        var application = await db.Applications.AsNoTracking()
            .Include(a => a.Versions.Where(v => v.State == SettingsVersionState.Active)).ThenInclude(v => v.Roles)
            .SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (application?.CurrentActive is not { } active)
        {
            return null;
        }

        return new ApplicationApprovers(
            application.Id,
            active.VersionNumber,
            active.ContentHash,
            active.Owner,
            [.. active.Roles.OrderBy(r => r.RoleTypeId).Select(r => new ApplicationRoleHolding(
                r.RoleTypeId,
                (ApplicationRoleRequirement)(int)r.Requirement,
                r.HolderKind is { } kind ? (ApplicationRoleHolderKind)(int)kind : null,
                r.HolderId))]);
    }
}
