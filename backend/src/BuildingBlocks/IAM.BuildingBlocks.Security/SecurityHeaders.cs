using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace IAM.BuildingBlocks.Security;

/// <summary>Baseline response security headers for API and BFF hosts (ADR-0013 gateway requirements).</summary>
public static class SecurityHeaders
{
    /// <summary>
    /// Adds the headers just before the response starts, so they are present on every response, including error responses
    /// the exception handler writes after clearing the response.
    /// </summary>
    public static IApplicationBuilder UseIamSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(static (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                Apply(((HttpResponse)state).Headers);
                return Task.CompletedTask;
            }, context.Response);
            return next(context);
        });
    }

    private static void Apply(IHeaderDictionary headers)
    {
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        // API/BFF responses are data, not documents; the SPA's own CSP is set with the UI (S1-09).
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        headers.CacheControl = "no-store";
    }
}
