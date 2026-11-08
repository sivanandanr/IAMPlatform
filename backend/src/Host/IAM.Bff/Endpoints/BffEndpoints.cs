using System.Security.Claims;
using IAM.Bff.Authentication;
using IAM.Bff.Sessions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IAM.Bff.Endpoints;

/// <summary>Session endpoints used by the SPA: login, current user (+ antiforgery token), logout.</summary>
internal static class BffEndpoints
{
    public static IEndpointRouteBuilder MapBffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/bff");

        group.MapGet("/login", Login).AllowAnonymous();
        group.MapGet("/user", GetUser).RequireAuthorization(BffPolicies.Session);
        group.MapPost("/logout", LogoutAsync).RequireAuthorization(BffPolicies.Session);

        return endpoints;
    }

    /// <summary>Only same-site relative paths are accepted as return URLs (open-redirect protection).</summary>
    internal static bool IsLocalReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && url.Length <= 2048
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Any(char.IsControl);

    private static IResult Login(string? returnUrl)
    {
        var target = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;
        if (!IsLocalReturnUrl(target))
        {
            return TypedResults.BadRequest();
        }

        return TypedResults.Challenge(new AuthenticationProperties { RedirectUri = target }, [OpenIdConnectDefaults.AuthenticationScheme]);
    }

    private static Ok<BffUserResponse> GetUser(HttpContext context, ClaimsPrincipal user, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(BffAuthentication.XsrfTokenCookieName, tokens.RequestToken ?? string.Empty, new CookieOptions
        {
            HttpOnly = false, // the SPA reads it and echoes it in the X-XSRF-TOKEN header
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });

        return TypedResults.Ok(new BffUserResponse(
            user.FindFirstValue(BffClaimTypes.Subject) ?? string.Empty,
            user.FindFirstValue(BffClaimTypes.Name)));
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, ClaimsPrincipal user, BffSessionStore store)
    {
        var sessionId = user.FindFirstValue(BffClaimTypes.SessionId);
        var session = store.Get(sessionId);
        if (session?.Tokens.IdToken is { } idToken)
        {
            context.Items[BffAuthentication.IdTokenHintItemKey] = idToken;
        }

        store.Remove(sessionId); // server-side revocation: the cookie is useless from here on
        context.Response.Cookies.Delete(BffAuthentication.XsrfTokenCookieName, new CookieOptions { Secure = true, Path = "/" });
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);

        // Ends the IdP session too (RP-initiated logout); the IdP redirects back to /signout-callback-oidc.
        return TypedResults.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]);
    }
}

/// <summary>Response of <c>GET /bff/user</c>. Never contains tokens.</summary>
internal sealed record BffUserResponse(string Subject, string? Name);
