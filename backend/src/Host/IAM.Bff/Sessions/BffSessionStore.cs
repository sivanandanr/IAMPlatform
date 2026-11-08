using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace IAM.Bff.Sessions;

/// <summary>Tokens of one signed-in browser session. Immutable; replaced as a whole on refresh.</summary>
internal sealed record TokenSet(string AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset AccessTokenExpiresAt);

/// <summary>Bound from configuration section <c>Session</c>.</summary>
internal sealed class SessionSettings
{
    public const string SectionName = "Session";

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan AbsoluteTimeout { get; set; } = TimeSpan.FromHours(10);

    public int MaxSessions { get; set; } = 10_000;

    /// <summary>Refresh the access token when it expires within this window.</summary>
    public TimeSpan RefreshBeforeExpiry { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Server-side state of one browser session. Tokens never leave the server.</summary>
internal sealed class BffSession : IDisposable
{
    private TokenSet _tokens;
    private long _lastSeenTicks;

    public BffSession(string id, string subject, TokenSet tokens, DateTimeOffset createdAt)
    {
        Id = id;
        Subject = subject;
        _tokens = tokens;
        CreatedAt = createdAt;
        _lastSeenTicks = createdAt.UtcTicks;
    }

    public string Id { get; }

    public string Subject { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastSeenAt => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);

    public TokenSet Tokens
    {
        get => Volatile.Read(ref _tokens);
        set => Volatile.Write(ref _tokens, value);
    }

    /// <summary>Serialises refreshes so a rotated refresh token is used exactly once.</summary>
    public SemaphoreSlim RefreshLock { get; } = new(1, 1);

    public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastSeenTicks, now.UtcTicks);

    public void Dispose() => RefreshLock.Dispose();
}

/// <summary>
/// S1-05 in-memory session registry (owner decision 2026-11-08): single instance only; sessions are lost on restart.
/// Enforces idle and absolute timeouts and a capacity bound. Removing a session revokes the browser cookie,
/// because every request re-validates the cookie's session id against this store.
/// </summary>
internal sealed class BffSessionStore(TimeProvider timeProvider, IOptions<SessionSettings> options)
{
    private readonly ConcurrentDictionary<string, BffSession> _sessions = new(StringComparer.Ordinal);
    private readonly SessionSettings _settings = options.Value;

    public int Count => _sessions.Count;

    /// <summary>Creates a session, or returns null when the capacity bound is reached (fail closed).</summary>
    public BffSession? Create(string subject, TokenSet tokens)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentNullException.ThrowIfNull(tokens);
        PurgeExpired();
        if (_sessions.Count >= _settings.MaxSessions)
        {
            return null;
        }

        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var session = new BffSession(id, subject, tokens, timeProvider.GetUtcNow());
        return _sessions.TryAdd(id, session) ? session : null;
    }

    /// <summary>Returns a live session and records activity, or null if unknown or expired (expired sessions are removed).</summary>
    public BffSession? Get(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (IsExpired(session, now))
        {
            Remove(id);
            return null;
        }

        session.Touch(now);
        return session;
    }

    public void Remove(string? id)
    {
        if (!string.IsNullOrEmpty(id) && _sessions.TryRemove(id, out var session))
        {
            session.Dispose();
        }
    }

    private bool IsExpired(BffSession session, DateTimeOffset now) =>
        now - session.CreatedAt >= _settings.AbsoluteTimeout || now - session.LastSeenAt >= _settings.IdleTimeout;

    private void PurgeExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (id, session) in _sessions)
        {
            if (IsExpired(session, now))
            {
                Remove(id);
            }
        }
    }
}
