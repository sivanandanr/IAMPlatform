using IAM.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IAM.BuildingBlocks.Messaging.RabbitMq;

/// <summary>What one relay pass did, for logs and tests. Counts only.</summary>
public sealed record OutboxRelayPass(int Due, int Sent, int Failed, int Skipped, int Parked);

/// <summary>
/// Sends one module's outbox to the broker (ADR-0006, DL-077, DL-079), with that module's own database login. Each due
/// message is first claimed (a lease set, saved under the row's version, so two relays can't both claim it), then
/// published and confirmed, then marked dispatched. Only a failed send counts as an attempt: it waits 2, 4, 8 … seconds,
/// and after <see cref="OutboxRelaySettings.MaxAttempts"/> the message is parked for people to look at. A row whose content
/// breaks the envelope rules (written by SQL, say) is parked at once. Delivery is at least once: a relay that stops after
/// the broker confirmed but before saving sends the message again after the lease.
/// </summary>
public sealed partial class OutboxRelay<TContext>(
    IServiceScopeFactory scopes,
    IMessagePublisher publisher,
    OutboxRelaySettings settings,
    TimeProvider clock,
    ILogger<OutboxRelay<TContext>> logger)
    where TContext : DbContext
{
    public async Task<OutboxRelayPass> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var outbox = db.Set<OutboxMessageRow>();
        var now = clock.GetUtcNow();
        var max = settings.MaxAttempts;
        var due = await outbox
            .Where(m => m.DispatchedAtUtc == null && m.Attempts < max && (m.NextAttemptAtUtc == null || m.NextAttemptAtUtc <= now))
            .OrderBy(m => m.OccurredAtUtc)
            .ThenBy(m => m.MessageId)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int sent = 0, failed = 0, skipped = 0, parked = 0;
        foreach (var message in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Claim: leased before anything is sent; an attempt is counted only if the send fails.
            message.NextAttemptAtUtc = clock.GetUtcNow() + settings.Lease;
            if (!await TrySaveAsync(db, message, cancellationToken).ConfigureAwait(false))
            {
                skipped++;
                continue;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.PublishTimeout);
                await publisher.PublishAsync(message.ToEnvelope(), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Content that breaks the rules will never send: parked now rather than retried.
                message.Attempts = ex is InvalidMessageException ? max : message.Attempts + 1;
                message.LastError = Code(ex);
                message.NextAttemptAtUtc = clock.GetUtcNow() + OutboxRelaySettings.Backoff(message.Attempts);
                if (!await TrySaveAsync(db, message, cancellationToken).ConfigureAwait(false))
                {
                    // Another relay moved it meanwhile; its outcome is that relay's to report.
                    skipped++;
                    continue;
                }

                failed++;
                if (message.Attempts >= max)
                {
                    parked++;
                    LogParked(logger, message.MessageId, message.Type, message.Attempts, message.LastError);
                }
                else
                {
                    LogFailed(logger, message.MessageId, message.Type, message.Attempts, message.LastError);
                }

                continue;
            }

            message.DispatchedAtUtc = clock.GetUtcNow();
            message.LastError = null;
            if (await TrySaveAsync(db, message, cancellationToken).ConfigureAwait(false))
            {
                sent++;
            }
            else
            {
                // Sent but not marked: it goes again after the lease, and consumers deduplicate on its id.
                skipped++;
            }
        }

        return new OutboxRelayPass(due.Count, sent, failed, skipped, parked);
    }

    /// <summary>A failure's code (the broker's refusal, or the type name), bounded, never its message text (which can carry hosts or data).</summary>
    private static string Code(Exception ex)
    {
        var name = ex is InvalidMessageException ? "InvalidMessage" : RabbitMqTopology.ErrorCode(ex);
        return name.Length <= OutboxModel.MaxErrorLength ? name : name[..OutboxModel.MaxErrorLength];
    }

    private static async Task<bool> TrySaveAsync(TContext db, OutboxMessageRow message, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another relay claimed or moved it first.
            db.Entry(message).State = EntityState.Detached;
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({Type}) failed to send, attempt {Attempts}: {Error}; retried later.")]
    private static partial void LogFailed(ILogger logger, Guid messageId, string type, int attempts, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({Type}) failed {Attempts} times ({Error}) and is parked: it will not be sent until someone acts.")]
    private static partial void LogParked(ILogger logger, Guid messageId, string type, int attempts, string? error);
}

/// <summary>Runs <see cref="OutboxRelay{TContext}"/> every interval until the host stops (DL-077).</summary>
public sealed partial class OutboxRelayWorker<TContext>(OutboxRelay<TContext> relay, OutboxRelaySettings settings, TimeProvider clock, ILogger<OutboxRelayWorker<TContext>> logger)
    : BackgroundService
    where TContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.Interval, clock);
        do
        {
            try
            {
                var pass = await relay.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (pass.Due > 0)
                {
                    LogPass(logger, pass.Due, pass.Sent, pass.Failed, pass.Skipped, pass.Parked);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A pass that fails as a whole (the database is down, say) waits for the next tick.
                LogPassFailed(logger, ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox: {Due} due, {Sent} sent, {Failed} failed, {Skipped} skipped, {Parked} parked.")]
    private static partial void LogPass(ILogger logger, int due, int sent, int failed, int skipped, int parked);

    [LoggerMessage(Level = LogLevel.Error, Message = "An outbox relay pass failed with {ErrorType}; trying again next interval.")]
    private static partial void LogPassFailed(ILogger logger, string errorType);
}
