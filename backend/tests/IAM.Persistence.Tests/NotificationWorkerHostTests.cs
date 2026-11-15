using IAM.BuildingBlocks.Messaging.RabbitMq;
using IAM.Workers.Notification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace IAM.Persistence.Tests;

/// <summary>The notification worker refuses to start without its settings (DL-078). No database or broker needed.</summary>
public sealed class NotificationWorkerHostTests
{
    private static readonly KeyValuePair<string, string?>[] Complete =
    [
        new("IAM_DB_PROVIDER", "PostgreSql"),
        new("ConnectionStrings:notification", "Host=localhost;Database=iam;Username=u;Password=p"),
        new("Messaging:RabbitMq:Host", "localhost"),
        new("Messaging:RabbitMq:VirtualHost", "iam"),
        new("Messaging:RabbitMq:UserName", "iam-notification"),
        new("Messaging:RabbitMq:Password", "p"),
        new("Messaging:RabbitMq:UseTls", "false"),
    ];

    [Fact]
    public void ItStartsOnlyWithEverySetting()
    {
        using (var host = NotificationWorkerHost.Build([], Complete))
        {
            host.Services.GetRequiredService<ConsumerSettings>().DeliveryLimit.ShouldBe(5);
            host.Services.GetServices<IHostedService>().Count().ShouldBe(1, "the consumer");
        }

        Should.Throw<Exception>(() => NotificationWorkerHost.Build([], Complete.Where(s => s.Key != "IAM_DB_PROVIDER")));
        Should.Throw<InvalidOperationException>(() => NotificationWorkerHost.Build([], Complete.Where(s => s.Key != "ConnectionStrings:notification")))
            .Message.ShouldContain("notification");
        foreach (var key in new[] { "Host", "VirtualHost", "UserName", "Password" })
        {
            Should.Throw<InvalidOperationException>(() => NotificationWorkerHost.Build([], Complete.Where(s => s.Key != $"Messaging:RabbitMq:{key}")), key);
        }

        Should.Throw<InvalidOperationException>(() => NotificationWorkerHost.Build([], [.. Complete.Where(s => s.Key != "Messaging:RabbitMq:UserName"), new("Messaging:RabbitMq:UserName", "guest")]));
        Should.Throw<ArgumentOutOfRangeException>(() => NotificationWorkerHost.Build([], [.. Complete, new("Notification:Consumer:DeliveryLimit", "0")]));
    }
}
