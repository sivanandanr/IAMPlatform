using IAM.BuildingBlocks.Security;
using IAM.Modules.Administration.Application.Access;
using IAM.Modules.Administration.Domain.Access;

namespace IAM.Modules.Administration.Infrastructure.Access;

/// <summary>
/// Answers permission checks from the stored access model (P0-013 slice 3). Read once per request scope and never cached
/// across requests (DL-059f), so a revoked role stops working on the next request. Unknown actions are refused.
/// Actions everyone signed in has (ActionCatalog "everyone") don't depend on the store and are answered without reading
/// it. A damaged store throws <see cref="AccessStoreIntegrityException"/>, which the API turns into a 503: never a grant.
/// </summary>
public sealed class AccessPermissionEvaluator(IAccessAdministration access) : IPermissionEvaluator
{
    private AccessModel? _model;

    public async Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(subject) || !ActionCatalog.IsKnown(action))
        {
            return false;
        }

        if (ActionCatalog.IsEveryone(action))
        {
            return true;
        }

        _model ??= await access.CurrentModelAsync(cancellationToken).ConfigureAwait(false);
        return _model.Can(subject, action);
    }
}
