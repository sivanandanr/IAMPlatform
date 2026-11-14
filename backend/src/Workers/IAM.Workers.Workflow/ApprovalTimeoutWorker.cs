using IAM.Modules.Workflow.Application.Approvals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IAM.Workers.Workflow;

/// <summary>The timeout worker's settings (<c>Workflow:Timeouts</c>), checked when the worker is built (DL-074).</summary>
public sealed class ApprovalTimeoutSettings
{
    public const string Section = "Workflow:Timeouts";
    public const int DefaultIntervalSeconds = 60;
    public const int DefaultBatchSize = 50;

    public ApprovalTimeoutSettings(int? intervalSeconds = null, int? batchSize = null)
    {
        Interval = TimeSpan.FromSeconds(intervalSeconds ?? DefaultIntervalSeconds);
        BatchSize = batchSize ?? DefaultBatchSize;
        if (Interval < TimeSpan.FromSeconds(5) || Interval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "Workflow:Timeouts:IntervalSeconds must be 5 to 3600.");
        }

        if (BatchSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Workflow:Timeouts:BatchSize must be 1 to 500.");
        }
    }

    public TimeSpan Interval { get; }

    public int BatchSize { get; }
}

/// <summary>What one pass did, for logs and tests. Counts only.</summary>
public sealed record ApprovalTimeoutPass(int Due, int Applied, int Skipped, int Failed);

/// <summary>
/// One pass of the timeout worker (DL-074): finds instances whose deadlines have passed and applies their timeouts through
/// <see cref="IApprovalWorkflow.ApplyTimeoutsAsync"/>, each in its own scope and transaction, at the server's time. A
/// conflict with another writer is left for the next pass. A damaged instance is audited by the service once and then
/// set aside until restart, so it isn't audited again every pass. Nothing here can approve: timeouts only hold, reject or
/// cancel (ADR-0005).
/// </summary>
public sealed partial class ApprovalTimeoutSweeper(IServiceScopeFactory scopes, ApprovalTimeoutSettings settings, TimeProvider clock, ILogger<ApprovalTimeoutSweeper> logger)
{
    private readonly HashSet<Guid> _setAside = [];

    /// <summary>Instances set aside as damaged since the worker started.</summary>
    public IReadOnlySet<Guid> SetAside => _setAside;

    public async Task<ApprovalTimeoutPass> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DueApproval> due;
        using (var scope = scopes.CreateScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<IDueApprovals>()
                .DueAsync(clock.GetUtcNow(), settings.BatchSize, _setAside, cancellationToken).ConfigureAwait(false);
        }

        int applied = 0, skipped = 0, failed = 0;
        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = scopes.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<IApprovalWorkflow>()
                    .ApplyTimeoutsAsync(item.InstanceId, item.Version, cancellationToken).ConfigureAwait(false);
                applied++;
            }
            catch (ApprovalInstanceDamagedException)
            {
                _setAside.Add(item.InstanceId);
                skipped++;
                LogDamaged(logger, item.InstanceId);
            }
            catch (Exception ex) when (ex is ApprovalConflictException || WorkflowApi.Classify(ex, out _) is WorkflowFailureKind.Conflict or WorkflowFailureKind.WrongState)
            {
                // Someone else changed it first; it is due again on the next pass if it still is.
                skipped++;
                LogConflict(logger, item.InstanceId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogFailed(logger, item.InstanceId, ex.GetType().Name);
            }
        }

        return new ApprovalTimeoutPass(due.Count, applied, skipped, failed);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval instance {InstanceId} does not replay; set aside until restart (audited as damaged).")]
    private static partial void LogDamaged(ILogger logger, Guid instanceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Approval instance {InstanceId} changed while applying timeouts; retried next pass.")]
    private static partial void LogConflict(ILogger logger, Guid instanceId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Applying timeouts to approval instance {InstanceId} failed with {ErrorType}.")]
    private static partial void LogFailed(ILogger logger, Guid instanceId, string errorType);
}

/// <summary>Runs <see cref="ApprovalTimeoutSweeper"/> every interval until the host stops (DL-074).</summary>
public sealed partial class ApprovalTimeoutWorker(ApprovalTimeoutSweeper sweeper, ApprovalTimeoutSettings settings, TimeProvider clock, ILogger<ApprovalTimeoutWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.Interval, clock);
        do
        {
            try
            {
                var pass = await sweeper.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (pass.Due > 0)
                {
                    LogPass(logger, pass.Due, pass.Applied, pass.Skipped, pass.Failed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A pass that fails as a whole (the database is down, say) waits for the next tick.
                LogPassFailed(logger, ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Approval timeouts: {Due} due, {Applied} applied, {Skipped} skipped, {Failed} failed.")]
    private static partial void LogPass(ILogger logger, int due, int applied, int skipped, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "An approval timeout pass failed with {ErrorType}; trying again next interval.")]
    private static partial void LogPassFailed(ILogger logger, string errorType);
}
