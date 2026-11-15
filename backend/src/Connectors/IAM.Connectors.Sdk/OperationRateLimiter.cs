namespace IAM.Connectors.Sdk;

/// <summary>
/// A token bucket of <c>perSecond</c> operations (DL-082): full at the start, refilled continuously, never above one
/// second's worth, so a burst can't exceed the limit and an idle connector doesn't bank more. Time comes from a
/// <see cref="TimeProvider"/>, so tests control it.
/// </summary>
public sealed class OperationRateLimiter
{
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private double _tokens;
    private long _last;

    public OperationRateLimiter(int perSecond, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perSecond, 1);
        ArgumentNullException.ThrowIfNull(clock);
        PerSecond = perSecond;
        _clock = clock;
        _tokens = perSecond;
        _last = clock.GetTimestamp();
    }

    public int PerSecond { get; }

    /// <summary>Takes one operation's worth if there is one; never waits.</summary>
    public bool TryAcquire()
    {
        lock (_sync)
        {
            var now = _clock.GetTimestamp();
            var elapsed = _clock.GetElapsedTime(_last, now).TotalSeconds;
            _last = now;
            _tokens = Math.Min(PerSecond, _tokens + (elapsed * PerSecond));
            if (_tokens < 1)
            {
                return false;
            }

            _tokens -= 1;
            return true;
        }
    }
}
