namespace IAM.Modules.Administration.Infrastructure.Access;

/// <summary>
/// At most one audit event per key in each window, per process; the next event written for a key carries how many were
/// left out before it (R-033). The thing being audited is still reported to every caller; only the evidence is thinned.
/// </summary>
public abstract class KeyedAuditThrottle(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, (DateTimeOffset Until, int Suppressed)> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>True if this event should be written now; <paramref name="suppressed"/> is how many were left out before it.</summary>
    public bool TryEnter(string key, out int suppressed)
    {
        ArgumentNullException.ThrowIfNull(key);
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && now < entry.Until)
            {
                _entries[key] = (entry.Until, entry.Suppressed + 1);
                suppressed = 0;
                return false;
            }

            suppressed = entry.Suppressed;
            _entries[key] = (now + Window, 0);
            return true;
        }
    }
}

/// <summary>Integrity-failure audit events and log lines: keyed by step, failed check and request (DL-063).</summary>
public sealed class AccessIntegrityAuditThrottle(TimeProvider clock) : KeyedAuditThrottle(clock);

/// <summary>Permission-check denials: keyed by person and action, so a loop can't flood the audit log (DL-063b).</summary>
public sealed class AccessDenialAuditThrottle(TimeProvider clock) : KeyedAuditThrottle(clock);
