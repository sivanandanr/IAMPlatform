using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Identity.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.Modules.Identity.Infrastructure;

public static class IdentityInfrastructure
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddIamDbContext<IdentityDbContext>(IdentityDbContext.SchemaName);
    }
}
