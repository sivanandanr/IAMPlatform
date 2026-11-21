using IAM.BuildingBlocks.Persistence;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Workflow.Application.Approvals;
using IAM.Modules.Workflow.Application.Approvers;
using IAM.Modules.Workflow.Contracts;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using IAM.Modules.Workflow.Infrastructure.Approvers;
using IAM.Modules.Workflow.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAM.Modules.Workflow.Infrastructure;

/// <summary>
/// Registers durable approval workflows (DL-072), approver resolution (DL-069) and the escalation gate (DL-070). Called by
/// <c>WorkflowModule</c> in the API and by the workflow worker (DL-074); either needs <c>ConnectionStrings:workflow</c>, and
/// the Catalog and Organization infrastructure for resolution.
/// </summary>
public static class WorkflowInfrastructure
{
    /// <summary>The configured fallback workgroup's key (DL-068c); <c>iam_managers</c> when not set.</summary>
    public const string FallbackWorkgroupSetting = "Workflow:FallbackWorkgroup";

    public static IServiceCollection AddWorkflowInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIamDbContext<WorkflowDbContext>(WorkflowDbContext.SchemaName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new ApproverResolutionSettings(sp.GetRequiredService<IConfiguration>()[FallbackWorkgroupSetting]));
        services.TryAddScoped<IApproverResolver, ApproverResolver>();
        services.TryAddScoped<IApprovalEscalation, ApprovalEscalationService>();
        services.TryAddScoped<ApprovalWorkflowService>();
        services.TryAddScoped<IApprovalWorkflow>(sp => sp.GetRequiredService<ApprovalWorkflowService>());
        services.TryAddScoped<IApprovalStageChecks, ApprovalStageChecks>();
        services.TryAddScoped<IDueApprovals, DueApprovals>();
        foreach (var action in WorkflowAuditActions.All)
        {
            services.AddSingleton(new AuditActionRegistration(action));
        }

        return services;
    }
}
