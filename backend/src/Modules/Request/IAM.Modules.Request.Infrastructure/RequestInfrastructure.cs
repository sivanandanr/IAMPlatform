using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Request.Application.Items;
using IAM.Modules.Request.Contracts;
using IAM.Modules.Request.Infrastructure.Items;
using IAM.Modules.Request.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Request.Infrastructure;

/// <summary>Registers request items (DL-091). Called by <c>RequestModule</c>; the host then needs <c>ConnectionStrings:request</c>.</summary>
public static class RequestInfrastructure
{
    public static IServiceCollection AddRequestInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<RequestDbContext>(RequestDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IRequestItems, RequestItemService>();
        services.TryAddScoped<IRequestItemSource, RequestItemSource>();
        foreach (var action in RequestAuditActions.All)
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }

        return services;
    }

    /// <summary>
    /// Registers only the read of frozen items for provisioning (DL-093), for a host that doesn't take requests. It needs
    /// <c>ConnectionStrings:request</c>.
    /// </summary>
    public static IServiceCollection AddRequestItemSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<RequestDbContext>(RequestDbContext.SchemaName);
        services.TryAddScoped<IRequestItemSource, RequestItemSource>();
        return services;
    }
}
