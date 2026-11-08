using IAM.Bff.Authentication;
using IAM.Bff.Endpoints;
using IAM.Bff.Proxy;
using IAM.Bff.Sessions;
using IAM.BuildingBlocks.Security;

// S1-05 browser edge (ADR-0012 BFF, ADR-0013 separate BFF/YARP host). Browser -> IAM.Bff (cookie session)
// -> IAM.Api (bearer token, private network). Sprint 1 limits: in-memory sessions (single instance, lost on
// restart), no rate limiting, no step-up/MFA policy, no SPA hosting yet. Not production-ready.
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1 * 1024 * 1024);
builder.Services.AddBffSessions(builder.Configuration);
builder.Services.AddBffAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddHealthChecks();
builder.Services.AddReverseProxy().AddIamApiProxy(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseIamSecurityHeaders();
app.UseAuthentication();
app.UseBffAntiforgery();
app.UseAuthorization();

app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapBffEndpoints();
app.MapReverseProxy(proxy => proxy.UseSessionAccessToken());

await app.RunAsync().ConfigureAwait(false);
