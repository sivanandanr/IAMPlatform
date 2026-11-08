using System.Net.Http.Headers;
using System.Security.Claims;
using IAM.Bff.Authentication;
using IAM.Bff.Sessions;
using Yarp.ReverseProxy.Transforms;

namespace IAM.Bff.Proxy;

/// <summary>
/// YARP forwarding of <c>/api/*</c> to IAM.Api (ADR-0013). The BFF attaches the session's access token server-side and
/// strips every client-supplied credential or identity header, so the browser can never choose what the API sees.
/// </summary>
internal static class ApiProxy
{
    private const string AccessTokenItemKey = "iam.bff.access_token";

    /// <summary>Exact request headers never forwarded to the API.</summary>
    internal static readonly string[] StrippedRequestHeaders =
    [
        "Authorization", "Cookie", "Proxy-Authorization", "Forwarded",
        BffAuthentication.AntiforgeryHeaderName,
    ];

    /// <summary>Request header prefixes never forwarded (identity spoofing). YARP sets X-Forwarded-* itself.</summary>
    internal static readonly string[] StrippedRequestHeaderPrefixes = ["X-User", "X-Roles", "X-Role", "X-Original-", "X-Client-", "X-Auth"];

    public static IReverseProxyBuilder AddIamApiProxy(this IReverseProxyBuilder builder, IConfiguration configuration) =>
        builder
            .LoadFromConfig(configuration.GetSection("ReverseProxy"))
            .AddTransforms(static context =>
            {
                context.CopyRequestHeaders = true;
                context.CopyResponseHeaders = true;
                context.AddRequestTransform(static transform =>
                {
                    var headers = transform.ProxyRequest.Headers;
                    foreach (var name in StrippedRequestHeaders)
                    {
                        headers.Remove(name);
                    }

                    foreach (var name in headers.Select(h => h.Key).Where(IsStrippedPrefix).ToList())
                    {
                        headers.Remove(name);
                    }

                    if (transform.HttpContext.Items[AccessTokenItemKey] is string token)
                    {
                        headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }

                    return ValueTask.CompletedTask;
                });
                context.AddResponseTransform(static transform =>
                {
                    // The API must not set cookies on the BFF origin.
                    transform.HttpContext.Response.Headers.Remove("Set-Cookie");
                    return ValueTask.CompletedTask;
                });
            });

    /// <summary>Proxy-pipeline step: resolves a valid access token for the session, or ends with 401 (nothing is forwarded).</summary>
    public static IReverseProxyApplicationBuilder UseSessionAccessToken(this IReverseProxyApplicationBuilder proxy)
    {
        proxy.Use(static async (context, next) =>
        {
            var store = context.RequestServices.GetRequiredService<BffSessionStore>();
            var session = store.Get(context.User.FindFirstValue(BffClaimTypes.SessionId));
            var token = session is null
                ? null
                : await context.RequestServices.GetRequiredService<SessionTokenService>()
                    .GetAccessTokenAsync(session, context.RequestAborted).ConfigureAwait(false);

            if (token is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.Items[AccessTokenItemKey] = token;
            await next().ConfigureAwait(false);
        });
        return proxy;
    }

    private static bool IsStrippedPrefix(string name) =>
        StrippedRequestHeaderPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
