using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace IAM.BuildingBlocks.Security;

/// <summary>Baseline response security headers for API and BFF hosts (ADR-0013 gateway requirements).</summary>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseIamSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(static (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "DENY";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            // API/BFF responses are data, not documents; the SPA's own CSP is set with the UI (S1-09).
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            headers.CacheControl = "no-store";
            return next(context);
        });
    }
}
