using System.Security.Cryptography;
using System.Text.Json;
using IAM.BuildingBlocks.Messaging.RabbitMq;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// One throwaway RabbitMQ per test class that needs it (DL-077 to DL-079), the same image compose pins, set up as an
/// operator would: the reviewed topology from <c>infra/rabbitmq/definitions.json</c> and each module's broker user with
/// exactly the permissions in <c>infra/rabbitmq/permissions.json</c>. The administrator is the tests' own user, never a
/// module's. TLS is off because the broker is on this machine, which <see cref="RabbitMqSettings"/> allows.
/// </summary>
public sealed class RabbitMqBroker : IAsyncLifetime
{
    public const string VirtualHost = "iam";

    private const string Image = "rabbitmq:4.3.7-management-alpine@sha256:eb338b3fc8f5c251028c0022ac0de3fe1db2d599d1c6a08b45898f756f248881";
    private const string Admin = "iam-test-admin";

    private readonly string _password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private RabbitMqContainer? _container;
    private int _port;

    /// <summary>The broker administrator: for setting up and inspecting, never as a module.</summary>
    public RabbitMqSettings Settings { get; private set; } = null!;

    /// <summary>The workflow module's own broker user (permissions.json).</summary>
    public RabbitMqSettings Workflow { get; private set; } = null!;

    /// <summary>The notification module's own broker user (permissions.json).</summary>
    public RabbitMqSettings Notification { get; private set; } = null!;

    /// <summary>The request module's own broker user (permissions.json).</summary>
    public RabbitMqSettings Request { get; private set; } = null!;

    /// <summary>The provisioning module's own broker user (permissions.json).</summary>
    public RabbitMqSettings Provisioning { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _container = new RabbitMqBuilder(Image).WithUsername(Admin).WithPassword(_password).Build();
        await _container.StartAsync();
        _port = _container.GetMappedPublicPort(5672);

        await RunAsync(["rabbitmqctl", "add_vhost", VirtualHost]);
        await RunAsync(["rabbitmqctl", "set_permissions", "-p", VirtualHost, Admin, ".*", ".*", ".*"]);
        await ImportDefinitionsAsync();
        Settings = new RabbitMqSettings("127.0.0.1", _port, VirtualHost, Admin, _password, useTls: false);

        using var permissions = JsonDocument.Parse(await File.ReadAllTextAsync(Repository("permissions.json")));
        var users = new Dictionary<string, RabbitMqSettings>(StringComparer.Ordinal);
        foreach (var user in permissions.RootElement.GetProperty("users").EnumerateArray())
        {
            var name = user.GetProperty("user").GetString()!;
            var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            await RunAsync(["rabbitmqctl", "add_user", name, password]);
            await RunAsync(["rabbitmqctl", "set_permissions", "-p", VirtualHost, name,
                user.GetProperty("configure").GetString()!, user.GetProperty("write").GetString()!, user.GetProperty("read").GetString()!]);
            foreach (var topic in user.GetProperty("topics").EnumerateArray())
            {
                await RunAsync(["rabbitmqctl", "set_topic_permissions", "-p", VirtualHost, name,
                    topic.GetProperty("exchange").GetString()!, topic.GetProperty("write").GetString()!, topic.GetProperty("read").GetString()!]);
            }

            users[name] = new RabbitMqSettings("127.0.0.1", _port, VirtualHost, name, password, useTls: false);
        }

        Workflow = users["iam-workflow"];
        Notification = users["iam-notification"];
        Request = users["iam-request"];
        Provisioning = users["iam-provisioning"];
    }

    /// <summary>Applies the definitions file again, restoring anything a test deleted.</summary>
    public async Task ImportDefinitionsAsync()
    {
        await _container!.CopyAsync(await File.ReadAllBytesAsync(Repository("definitions.json")), "/tmp/iam-definitions.json");
        await RunAsync(["rabbitmqctl", "import_definitions", "/tmp/iam-definitions.json"]);
    }

    /// <summary>The same broker on a port nothing listens on.</summary>
    public RabbitMqSettings Unreachable() => new("127.0.0.1", 1, VirtualHost, Workflow.UserName, Workflow.Password, useTls: false, connectTimeoutSeconds: 1);

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

    /// <summary>A file in <c>infra/rabbitmq</c>, found by walking up from the test output to the repository.</summary>
    public static string Repository(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "infra", "rabbitmq", file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"infra/rabbitmq/{file} not found above {AppContext.BaseDirectory}.");
    }

    private async Task RunAsync(IList<string> command)
    {
        var result = await _container!.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{command[1]} failed: {result.Stderr}");
        }
    }
}
