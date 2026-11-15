using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Notification.Infrastructure.Approvals;
using IAM.Modules.Notification.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Notification.Infrastructure;

/// <summary>Registers the Notification module (DL-078). The host then needs <c>ConnectionStrings:notification</c>.</summary>
public static class NotificationInfrastructure
{
    public static IServiceCollection AddNotificationInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<NotificationDbContext>(NotificationDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ApprovalTaskNotifications>();
        return services;
    }
}
