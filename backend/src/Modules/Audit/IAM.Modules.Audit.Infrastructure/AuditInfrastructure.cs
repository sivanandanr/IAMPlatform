using IAM.Modules.Audit.Contracts;
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
}
