using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Account.Application.Accounts;
using IAM.Modules.Account.Contracts;
using IAM.Modules.Account.Infrastructure.Accounts;
using IAM.Modules.Account.Infrastructure.Persistence;
using IAM.Modules.Audit.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Account.Infrastructure;

/// <summary>Registers accounts and naming rules (DL-089). Called by <c>AccountModule</c>; the host then needs <c>ConnectionStrings:account</c>.</summary>
public static class AccountInfrastructure
{
    public static IServiceCollection AddAccountInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<AccountDbContext>(AccountDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAccountAdministration, AccountAdministrationService>();
        services.TryAddScoped<IAccountDirectory, AccountDirectory>();
        services.TryAddScoped<IAccountNaming, AccountNaming>();
        services.TryAddScoped<IAccountProvisioning, AccountProvisioningService>();
        services.AddSingleton(new AuditActionRegistration(AccountProvisioningService.Created));
        foreach (var action in AccountAuditActions.All)
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }

        return services;
    }
}
