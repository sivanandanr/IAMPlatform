using System.Security.Cryptography;
using IAM.BuildingBlocks.Messaging.RabbitMq;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// One throwaway RabbitMQ per test class that needs it (DL-077), the same image compose pins. The test user is not
/// <c>guest</c>; TLS is off because the broker is on this machine, which <see cref="RabbitMqSettings"/> allows.
/// </summary>
public sealed class RabbitMqBroker : IAsyncLifetime
{
    private const string Image = "rabbitmq:4.3.7-management-alpine@sha256:eb338b3fc8f5c251028c0022ac0de3fe1db2d599d1c6a08b45898f756f248881";
    private const string User = "iam-workflow-test";

    private readonly string _password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private RabbitMqContainer? _container;

    public RabbitMqSettings Settings { get; private set; } = null!;

    /// <summary>The workflow module's own broker user: its exchange and the unrouted catch-all only (DL-078).</summary>
    public RabbitMqSettings Workflow { get; private set; } = null!;

    /// <summary>The notification module's own broker user: its queues and the dead-letter exchange, and reading the workflow exchange.</summary>
    public RabbitMqSettings Notification { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _container = new RabbitMqBuilder(Image).WithUsername(User).WithPassword(_password).Build();
        await _container.StartAsync();
        Settings = new RabbitMqSettings("127.0.0.1", _container.GetMappedPublicPort(5672), "/", User, _password, useTls: false);
        Workflow = await AddUserAsync("iam-workflow", @"^iam\.(workflow|unrouted)$", @"^iam\.(workflow|unrouted)$", @"^iam\.(workflow|unrouted)$");
        Notification = await AddUserAsync(
            "iam-notification", @"^iam\.(notification\..*|dlx)$", @"^iam\.(notification\..*|dlx)$", @"^iam\.(workflow|notification\..*|dlx)$");
    }

    /// <summary>
    /// A module's broker user with the permissions DL-078 gives it (configure, write and read, as RabbitMQ name patterns),
    /// set with <c>rabbitmqctl</c> as a deployment's definitions would.
    /// </summary>
    private async Task<RabbitMqSettings> AddUserAsync(string user, string configure, string write, string read)
    {
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await RunAsync(["rabbitmqctl", "add_user", user, password]);
        await RunAsync(["rabbitmqctl", "set_permissions", "-p", "/", user, configure, write, read]);
        return new RabbitMqSettings("127.0.0.1", Settings.Port, "/", user, password, useTls: false);
    }

    private async Task RunAsync(IList<string> command)
    {
        var result = await _container!.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{command[1]} failed: {result.Stderr}");
        }
    }

    /// <summary>The same broker on a port nothing listens on.</summary>
    public RabbitMqSettings Unreachable() => new("127.0.0.1", 1, "/", User, _password, useTls: false, connectTimeoutSeconds: 1);

    public Task<IConnection> ConnectAsync(CancellationToken cancellationToken) => ConnectAsync(Settings, cancellationToken);

    public static async Task<IConnection> ConnectAsync(RabbitMqSettings settings, CancellationToken cancellationToken) =>
        await new ConnectionFactory
        {
            HostName = settings.Host,
            Port = settings.Port,
            VirtualHost = settings.VirtualHost,
            UserName = settings.UserName,
            Password = settings.Password,
        }.CreateConnectionAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
