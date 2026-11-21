using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Audit.Infrastructure;

public static class AuditInfrastructure
{
    /// <summary>
    /// Registers <see cref="IAuditWriter"/>. The audit DbContext is not registered for the API yet: nothing reads audit
    /// rows in S1-07, and writes use the caller's transaction, so no <c>ConnectionStrings:audit</c> is needed.
    /// </summary>
    public static IServiceCollection AddAuditInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AuditActionCatalog>();
        services.TryAddScoped<IAuditWriter, AuditWriter>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="IAuditTrail"/> for a host that checks a module's log against the audit store (DL-093). The host
    /// then needs <c>ConnectionStrings:audit</c>: the audit login, which may only read audit rows.
    /// </summary>
    public static IServiceCollection AddAuditTrail(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<AuditDbContext>(AuditDbContext.SchemaName);
        services.TryAddScoped<IAuditTrail, AuditTrail>();
        return services;
    }
}
