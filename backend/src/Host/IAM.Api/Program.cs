using IAM.Modules.Account.Endpoints;
using IAM.Modules.Administration.Endpoints;
using IAM.Modules.Audit.Endpoints;
using IAM.Modules.Catalog.Endpoints;
using IAM.Modules.Governance.Endpoints;
using IAM.Modules.Identity.Endpoints;
using IAM.Modules.Integration.Endpoints;
using IAM.Modules.Lifecycle.Endpoints;
using IAM.Modules.Notification.Endpoints;
using IAM.Modules.Organization.Endpoints;
using IAM.Modules.Policy.Endpoints;
using IAM.Modules.Provisioning.Endpoints;
using IAM.Modules.Request.Endpoints;
using IAM.Modules.Workflow.Endpoints;

// S1-02 skeleton: composes the modules only. No endpoints, authentication or health checks yet;
// those arrive with S1-05 (BFF/OIDC, default-deny authorization, secured health). Not production-ready.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIdentityModule();
builder.Services.AddOrganizationModule();
builder.Services.AddCatalogModule();
builder.Services.AddAccountModule();
builder.Services.AddPolicyModule();
builder.Services.AddWorkflowModule();
builder.Services.AddRequestModule();
builder.Services.AddProvisioningModule();
builder.Services.AddGovernanceModule();
builder.Services.AddAuditModule();
builder.Services.AddIntegrationModule();
builder.Services.AddNotificationModule();
builder.Services.AddAdministrationModule();
builder.Services.AddLifecycleModule();

var app = builder.Build();

app.MapIdentityEndpoints();
app.MapOrganizationEndpoints();
app.MapCatalogEndpoints();
app.MapAccountEndpoints();
app.MapPolicyEndpoints();
app.MapWorkflowEndpoints();
app.MapRequestEndpoints();
app.MapProvisioningEndpoints();
app.MapGovernanceEndpoints();
app.MapAuditEndpoints();
app.MapIntegrationEndpoints();
app.MapNotificationEndpoints();
app.MapAdministrationEndpoints();
app.MapLifecycleEndpoints();

await app.RunAsync().ConfigureAwait(false);
