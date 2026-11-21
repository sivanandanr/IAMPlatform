using IAM.BuildingBlocks.Messaging.RabbitMq;
using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Request.Infrastructure.Persistence;
using IAM.Persistence.PostgreSql;
using IAM.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IAM.Workers.Request;

/// <summary>
/// Builds the request worker (DL-091): one database provider (<c>IAM_DB_PROVIDER</c>), the <c>request</c> connection string (the
/// module's own login), the broker (<c>Messaging:RabbitMq</c>, the request module's own broker user) and the relay settings
/// (<c>Request:Outbox</c>), all checked before it runs. It only sends what the request module already wrote to its outbox: it
/// reads no other schema and changes nothing but delivery fields. It writes as the fixed system actor <c>system:request-relay</c>.
/// </summary>
public static class RequestWorkerHost
{
    public const string Actor = "system:request-relay";

    public const string OutboxSection = "Request:Outbox";

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

        // Only the module's DbContext: the relay needs nothing else, so the worker needs no other login.
        services.AddIamDbContext<RequestDbContext>(RequestDbContext.SchemaName);
        services.AddSingleton(RabbitMqSettings.From(configuration));
        services.AddSingleton(OutboxRelaySettings.From(configuration, OutboxSection));
        services.AddSingleton<IMessagePublisher>(sp => new RabbitMqPublisher(sp.GetRequiredService<RabbitMqSettings>(), RequestEvents.Source));
        services.AddSingleton<OutboxRelay<RequestDbContext>>();
        services.AddHostedService<OutboxRelayWorker<RequestDbContext>>();

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
