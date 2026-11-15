using Microsoft.Extensions.Hosting;

namespace IAM.Workers.Notification;

/// <summary>
/// DL-078: consumes other modules' events into the Notification module. No HTTP surface. An explicit entry point rather
/// than top-level statements, so its type doesn't clash with other hosts' <c>Program</c> in test projects.
/// </summary>
internal static class WorkerEntryPoint
{
    public static async Task Main(string[] args)
    {
        using var host = NotificationWorkerHost.Build(args);
        await host.RunAsync().ConfigureAwait(false);
    }
}
