using IAM.Modules.Provisioning.Application.Items;
using IAM.Modules.Provisioning.Infrastructure.Items;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IAM.Workers.Provisioning;

/// <summary>
/// Runs due provisioning items every interval (DL-094), each pass in its own scope. A pass that fails is logged with its
/// exception type only and the next pass tries again; an item left running is taken over when its lease expires.
/// </summary>
public sealed partial class ProvisioningRunnerWorker(IServiceScopeFactory scopes, ProvisioningRunnerSettings settings, TimeProvider clock, ILogger<ProvisioningRunnerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var ran = await scope.ServiceProvider.GetRequiredService<ProvisioningRunner>().RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (ran > 0)
                {
                    LogRan(logger, ran);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogPassFailed(logger, ex.GetType().Name);
            }

            try
            {
                await Task.Delay(settings.Interval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning runner took {Count} items.")]
    private static partial void LogRan(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "A provisioning runner pass failed ({Error}); the next pass tries again.")]
    private static partial void LogPassFailed(ILogger logger, string error);
}
