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
/// conflict with another writer is left for the next pass. A damaged instance is set aside at once, and any other instance
/// after <see cref="MaxFailures"/> failed passes in a row (DL-075g), until restart: otherwise instances that always fail
/// would come first every pass and keep every other timeout waiting. Nothing here can approve: timeouts only hold, reject
/// or cancel (ADR-0005).
/// </summary>
public sealed partial class ApprovalTimeoutSweeper(IServiceScopeFactory scopes, ApprovalTimeoutSettings settings, TimeProvider clock, ILogger<ApprovalTimeoutSweeper> logger)
{
    /// <summary>Failed passes in a row after which an instance is set aside until restart.</summary>
    public const int MaxFailures = 3;

    private readonly HashSet<Guid> _setAside = [];
    private readonly Dictionary<Guid, int> _failures = [];

    /// <summary>Instances set aside since the worker started: damaged, or failing <see cref="MaxFailures"/> times in a row.</summary>
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
                _failures.Remove(item.InstanceId);
            }
            catch (ApprovalInstanceDamagedException)
            {
                _setAside.Add(item.InstanceId);
                _failures.Remove(item.InstanceId);
                skipped++;
                LogDamaged(logger, item.InstanceId);
            }
            catch (Exception ex) when (ex is ApprovalConflictException || WorkflowApi.Classify(ex, out _) is WorkflowFailureKind.Conflict)
            {
                // Someone else changed it first; it is due again on the next pass if it still is. Not a failure.
                skipped++;
                LogConflict(logger, item.InstanceId);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A refusal (a clock behind the last action, say) or an error: counted, and set aside if it keeps happening.
                failed++;
                LogFailed(logger, item.InstanceId, ex.GetType().Name);
                var count = _failures[item.InstanceId] = _failures.GetValueOrDefault(item.InstanceId) + 1;
                if (count >= MaxFailures)
                {
                    _setAside.Add(item.InstanceId);
                    _failures.Remove(item.InstanceId);
                    LogGivenUp(logger, item.InstanceId, count);
                }
            }
        }

        return new ApprovalTimeoutPass(due.Count, applied, skipped, failed);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval instance {InstanceId} does not replay; set aside until restart (audited as damaged).")]
    private static partial void LogDamaged(ILogger logger, Guid instanceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Approval instance {InstanceId} changed while applying timeouts; retried next pass.")]
    private static partial void LogConflict(ILogger logger, Guid instanceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applying timeouts to approval instance {InstanceId} failed with {ErrorType}.")]
    private static partial void LogFailed(ILogger logger, Guid instanceId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Approval instance {InstanceId} failed {Count} passes in a row; set aside until restart. Its timeouts are not applied: investigate it.")]
    private static partial void LogGivenUp(ILogger logger, Guid instanceId, int count);
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
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A pass that fails as a whole (a provider timeout surfaced as a cancellation included) (the database is down, say) waits for the next tick.
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
