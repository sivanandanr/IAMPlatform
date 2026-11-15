using Microsoft.Extensions.Hosting;

namespace IAM.Workers.Workflow;

/// <summary>
/// DL-074: applies approval deadlines in the background. No HTTP surface; nothing here can approve (ADR-0005). An explicit
/// entry point rather than top-level statements, so its type doesn't clash with the API's <c>Program</c> in test projects.
/// </summary>
internal static class WorkerEntryPoint
{
    public static async Task Main(string[] args)
    {
        using var host = WorkflowWorkerHost.Build(args);
        await host.RunAsync().ConfigureAwait(false);
    }
}
