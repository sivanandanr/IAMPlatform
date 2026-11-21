using Microsoft.Extensions.Hosting;

namespace IAM.Workers.Request;

/// <summary>
/// DL-091: sends the request module's outbox to the broker. No HTTP surface. An explicit entry point rather than top-level
/// statements, so its type doesn't clash with other hosts' <c>Program</c> in test projects.
/// </summary>
internal static class WorkerEntryPoint
{
    public static async Task Main(string[] args)
    {
        using var host = RequestWorkerHost.Build(args);
        await host.RunAsync().ConfigureAwait(false);
    }
}
