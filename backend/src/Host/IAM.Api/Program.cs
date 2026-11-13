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
// connection string per module schema, both checked at startup. P0-013 slice 3: every endpoint names the action it needs
// (RequirePermission), checked per request against the stored access model; an API test refuses any endpoint without one.
// Not production-ready (R-033).
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1 * 1024 * 1024);
builder.Services.AddIamApiAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddIamPermissionAuthorization();
builder.Services.AddIamRateLimits(builder.Configuration);
builder.Services.AddProblemDetails();
// Request JSON (BACKEND_INPUT_VALIDATION): bounded nesting, and duplicate keys refused, since which one wins is ambiguous.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.MaxDepth = 32;
    options.SerializerOptions.AllowDuplicateProperties = false;

    // Responses escape HTML-significant characters (<, >, &, quotes), so stored text can never read as markup.
    options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default;
});
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

// After every module's own handler: anything left is a plain 500 with nothing from the exception (R-033).
builder.Services.AddExceptionHandler<SafeExceptionHandler>();

var app = builder.Build();
ApiPersistence.EnsurePersistenceConfigured(app.Services);

app.UseExceptionHandler();
app.UseIamSecurityHeaders();
app.UseRouting();
app.UseIamRequestBodyLimits();
app.UseAuthentication();

// Before authorization: a flood is limited before any permission check reads the access model (DL-063c).
app.UseRateLimiter();
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
