using IAM.Api;
using IAM.BuildingBlocks.Security;
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

// S1-05: modules + bearer-token authentication + default-deny authorization. Reached only through the BFF
// (ADR-0013); not exposed to browsers. S1-06: one database provider (IAM_DB_PROVIDER, no default) and a
// connection string per module schema, both checked at startup. Authorization is authenticated-only for now: no permission model yet
// (DL-016), so no endpoint may expose sensitive data until per-action permissions exist. Not production-ready.
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1 * 1024 * 1024);
builder.Services.AddIamApiAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddIamPersistence(builder.Configuration);
builder.Services.AddHealthChecks();

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
ApiPersistence.EnsurePersistenceConfigured(app.Services);

app.UseIamSecurityHeaders();
app.UseAuthentication();
app.UseAuthorization();

// Liveness only: reveals nothing about dependencies. The single anonymous endpoint.
app.MapHealthChecks("/health/live").AllowAnonymous();

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
