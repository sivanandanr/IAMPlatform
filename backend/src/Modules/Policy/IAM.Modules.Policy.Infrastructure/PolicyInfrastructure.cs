using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Policy.Application.Matrix;
using IAM.Modules.Policy.Contracts;
using IAM.Modules.Policy.Infrastructure.Matrix;
using IAM.Modules.Policy.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Policy.Infrastructure;

/// <summary>Registers the approval matrix (DL-090). Called by <c>PolicyModule</c>; the host then needs <c>ConnectionStrings:policy</c>.</summary>
public static class PolicyInfrastructure
{
    public static IServiceCollection AddPolicyInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<PolicyDbContext>(PolicyDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<MatrixCompiler>();
        services.TryAddScoped<IApprovalMatrixAdministration, ApprovalMatrixService>();
        services.TryAddScoped<IApprovalMatrix, ApprovalMatrixSelector>();
        foreach (var action in MatrixAuditActions.All)
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }

        return services;
    }
}
