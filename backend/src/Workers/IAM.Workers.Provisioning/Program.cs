using Microsoft.Extensions.Hosting;

namespace IAM.Workers.Provisioning;

/// <summary>
/// DL-093: takes approvals' authorisations, checks each approval against the audit store and plans the connector operations.
/// No HTTP surface. An explicit entry point rather than top-level statements, so its type doesn't clash with other hosts'
/// <c>Program</c> in test projects.
/// </summary>
internal static class WorkerEntryPoint
{
    public static async Task Main(string[] args)
    {
        using var host = ProvisioningWorkerHost.Build(args);
        await host.RunAsync().ConfigureAwait(false);
    }
}
