using System.Net;
using Microsoft.Extensions.Configuration;

namespace IAM.BuildingBlocks.Messaging.RabbitMq;

/// <summary>
/// How a worker reaches the broker (<c>Messaging:RabbitMq</c>), checked when the worker is built (ADR-0006, DL-077). Each
/// module has its own broker user; <c>guest</c> is refused; TLS is required unless the broker is on this machine (local
/// development and tests). The password is never printed.
/// </summary>
public sealed class RabbitMqSettings
{
    public const string Section = "Messaging:RabbitMq";

    public RabbitMqSettings(string? host, int? port, string? virtualHost, string? userName, string? password, bool? useTls, int? connectTimeoutSeconds = null)
    {
        Host = Required(host, "Host");
        VirtualHost = Required(virtualHost, "VirtualHost");
        UserName = Required(userName, "UserName");
        Password = Required(password, "Password");
        UseTls = useTls ?? true;
        Port = port ?? (UseTls ? 5671 : 5672);
        ConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSeconds ?? 10);

        if (string.Equals(UserName, "guest", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{Section}:UserName must be the module's own broker user, never guest.");
        }

        if (!UseTls && !IsLoopback(Host))
        {
            throw new InvalidOperationException($"{Section}:UseTls may be false only for a broker on this machine.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), $"{Section}:Port must be 1 to 65535.");
        }

        if (ConnectTimeout < TimeSpan.FromSeconds(1) || ConnectTimeout > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeoutSeconds), $"{Section}:ConnectTimeoutSeconds must be 1 to 60.");
        }
    }

    public string Host { get; }

    public int Port { get; }

    public string VirtualHost { get; }

    public string UserName { get; }

    public string Password { get; }

    public bool UseTls { get; }

    public TimeSpan ConnectTimeout { get; }

    public static RabbitMqSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var s = configuration.GetSection(Section);
        return new RabbitMqSettings(
            s["Host"], s.GetValue<int?>("Port"), s["VirtualHost"], s["UserName"], s["Password"], s.GetValue<bool?>("UseTls"), s.GetValue<int?>("ConnectTimeoutSeconds"));
    }

    /// <summary>Host, port, virtual host and user; never the password.</summary>
    public override string ToString() => $"amqp{(UseTls ? "s" : string.Empty)}://{UserName}@{Host}:{Port}/{VirtualHost}";

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{Section}:{name} is required.") : value;

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}

/// <summary>A module's outbox relay settings (<c>{module}:Outbox</c>), checked when the worker is built (DL-077).</summary>
public sealed class OutboxRelaySettings
{
    public OutboxRelaySettings(int? intervalSeconds = null, int? batchSize = null, int? maxAttempts = null, int? publishTimeoutSeconds = null, int? leaseSeconds = null)
    {
        Interval = TimeSpan.FromSeconds(intervalSeconds ?? 5);
        BatchSize = batchSize ?? 100;
        MaxAttempts = maxAttempts ?? 10;
        PublishTimeout = TimeSpan.FromSeconds(publishTimeoutSeconds ?? 10);
        Lease = TimeSpan.FromSeconds(leaseSeconds ?? 60);
        Check(Interval >= TimeSpan.FromSeconds(1) && Interval <= TimeSpan.FromMinutes(5), nameof(intervalSeconds), "IntervalSeconds must be 1 to 300.");
        Check(BatchSize is >= 1 and <= 500, nameof(batchSize), "BatchSize must be 1 to 500.");
        Check(MaxAttempts is >= 1 and <= 50, nameof(maxAttempts), "MaxAttempts must be 1 to 50.");
        Check(PublishTimeout >= TimeSpan.FromSeconds(1) && PublishTimeout <= TimeSpan.FromMinutes(1), nameof(publishTimeoutSeconds), "PublishTimeoutSeconds must be 1 to 60.");
        // The lease covers the claim save, connecting, the send and the dispatched save (DL-079).
        Check(Lease >= (2 * PublishTimeout) + TimeSpan.FromSeconds(10) && Lease <= TimeSpan.FromMinutes(15), nameof(leaseSeconds), "LeaseSeconds must be at least twice the publish timeout plus 10, and at most 900.");
    }

    public TimeSpan Interval { get; }

    public int BatchSize { get; }

    /// <summary>Sends tried before a message is parked for people to look at.</summary>
    public int MaxAttempts { get; }

    public TimeSpan PublishTimeout { get; }

    /// <summary>How long a claimed message is left alone by other relays, in case this one stops mid-send.</summary>
    public TimeSpan Lease { get; }

    public static OutboxRelaySettings From(IConfiguration configuration, string section)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var s = configuration.GetSection(section);
        return new OutboxRelaySettings(
            s.GetValue<int?>("IntervalSeconds"), s.GetValue<int?>("BatchSize"), s.GetValue<int?>("MaxAttempts"), s.GetValue<int?>("PublishTimeoutSeconds"), s.GetValue<int?>("LeaseSeconds"));
    }

    /// <summary>The wait after a failed send: 2, 4, 8 … seconds, at most 15 minutes.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Clamp(attempts, 1, 20)), 900));

    private static void Check(bool ok, string name, string message)
    {
        if (!ok)
        {
            throw new ArgumentOutOfRangeException(name, message);
        }
    }
}
