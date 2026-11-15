using IAM.BuildingBlocks.Messaging.RabbitMq;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Notification.Infrastructure;
using IAM.Modules.Notification.Infrastructure.Approvals;
using IAM.Persistence.PostgreSql;
using IAM.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IAM.Workers.Notification;

/// <summary>
/// Builds the notification worker (DL-078): one database provider (<c>IAM_DB_PROVIDER</c>), the <c>notification</c>
/// connection string (the module's own login), the broker (<c>Messaging:RabbitMq</c>, the notification module's own
/// broker user) and the consumer settings (<c>Notification:Consumer</c>), all checked before it runs. It writes as the fixed
/// system actor <c>system:notifications</c> and has no permission to anything.
/// </summary>
public static class NotificationWorkerHost
{
    public const string Actor = "system:notifications";
    public const string ConsumerSection = "Notification:Consumer";

    public static IHost Build(string[] args, IEnumerable<KeyValuePair<string, string?>>? settings = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        var configuration = builder.Configuration;
        var services = builder.Services;
        var provider = DatabaseProviderSetting.Parse(configuration[DatabaseProviderSetting.Key]);
        services.AddSingleton<IIamDatabaseProvider>(provider switch
        {
            DatabaseProvider.SqlServer => new SqlServerDatabaseProvider(),
            DatabaseProvider.PostgreSql => new PostgreSqlDatabaseProvider(),
            _ => throw new InvalidOperationException("Unsupported database provider."),
        });
        services.AddSingleton<IIamConnectionStrings>(new ConfigurationConnectionStrings(configuration));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditActorAccessor>(new SystemActor(Actor));

        services.AddNotificationInfrastructure();
        services.AddSingleton(RabbitMqSettings.From(configuration));
        services.AddSingleton(ConsumerSettings.From(configuration, ConsumerSection));
        services.AddHostedService<RabbitMqConsumer<ApprovalTaskNotifications>>();

        var host = builder.Build();

        // Every schema the worker uses must have its own connection string, or it doesn't start (ADR-0017).
        var connectionStrings = host.Services.GetRequiredService<IIamConnectionStrings>();
        foreach (var schema in host.Services.GetService<IamPersistenceRegistry>()?.Schemas ?? [])
        {
            connectionStrings.ForSchema(schema);
        }

        return host;
    }

    private sealed class ConfigurationConnectionStrings(IConfiguration configuration) : IIamConnectionStrings
    {
        public string ForSchema(string schema)
        {
            var value = configuration.GetConnectionString(schema);
            return string.IsNullOrWhiteSpace(value)
                ? throw new InvalidOperationException(
                    $"ConnectionStrings:{schema} is not configured. Each module schema needs its own least-privilege login (ADR-0017).")
                : value;
        }
    }

    private sealed class SystemActor(string actor) : IAuditActorAccessor
    {
        public string? CurrentActor => actor;
    }
}
