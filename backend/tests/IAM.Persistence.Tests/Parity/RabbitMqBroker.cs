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

    public async ValueTask InitializeAsync()
    {
        _container = new RabbitMqBuilder(Image).WithUsername(User).WithPassword(_password).Build();
        await _container.StartAsync();
        Settings = new RabbitMqSettings("127.0.0.1", _container.GetMappedPublicPort(5672), "/", User, _password, useTls: false);
    }

    /// <summary>The same broker on a port nothing listens on.</summary>
    public RabbitMqSettings Unreachable() => new("127.0.0.1", 1, "/", User, _password, useTls: false, connectTimeoutSeconds: 1);

    public async Task<IConnection> ConnectAsync(CancellationToken cancellationToken) =>
        await new ConnectionFactory
        {
            HostName = Settings.Host,
            Port = Settings.Port,
            VirtualHost = Settings.VirtualHost,
            UserName = Settings.UserName,
            Password = Settings.Password,
        }.CreateConnectionAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
