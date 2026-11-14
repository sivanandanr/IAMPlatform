using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Workflow.Application.Approvals;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using IAM.Modules.Workflow.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Workflow.Infrastructure;

/// <summary>Registers durable approval workflows (DL-072). Called by <c>WorkflowModule</c>; the host then needs <c>ConnectionStrings:workflow</c>.</summary>
public static class WorkflowInfrastructure
{
    public static IServiceCollection AddWorkflowInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<WorkflowDbContext>(WorkflowDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IApprovalWorkflow, ApprovalWorkflowService>();
        foreach (var action in WorkflowAuditActions.All)
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }

        return services;
    }
}
