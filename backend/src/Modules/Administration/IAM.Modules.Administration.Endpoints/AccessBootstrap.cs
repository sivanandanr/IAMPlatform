using IAM.Modules.Administration.Application.Access;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Administration.Endpoints;

/// <summary>Outcome of the one-time bootstrap: a refusal code at most, never stored data.</summary>
public sealed record AccessBootstrapResult(bool Succeeded, AccessFailureKind Failure, string? RefusalCode);

/// <summary>
/// Composition entry point for the one-time access-control bootstrap (DL-059e, DL-062c), for the operator tool only: no
/// HTTP endpoint calls it. Names the first IAM Administrator and IAM Manager (two different people); refused once
/// anything is stored; audited either way, with the tool's server-side actor.
/// </summary>
public static class AccessBootstrap
{
    public static async Task<AccessBootstrapResult> RunAsync(IServiceProvider services, string administrator, string manager, BootstrapEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<IAccessAdministration>();
        try
        {
            await access.BootstrapAsync(administrator, manager, evidence, cancellationToken).ConfigureAwait(false);
            return new AccessBootstrapResult(true, AccessFailureKind.None, null);
        }
        catch (Exception ex) when (AccessFailures.Classify(ex, out _) != AccessFailureKind.None)
        {
            var kind = AccessFailures.Classify(ex, out var code);
            return new AccessBootstrapResult(false, kind, code);
        }
    }
}
