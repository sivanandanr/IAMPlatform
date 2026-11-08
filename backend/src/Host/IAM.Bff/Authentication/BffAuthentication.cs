using System.Globalization;
using System.Security.Claims;
using IAM.Bff.Sessions;
using IAM.BuildingBlocks.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace IAM.Bff.Authentication;

/// <summary>Bound from configuration section <c>Oidc</c>. The client secret comes from user-secrets / a secret store only.</summary>
internal sealed class OidcSettings
{
    public const string SectionName = "Oidc";

    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public bool RequireHttpsMetadata { get; set; } = true;
}

internal static class BffClaimTypes
{
    public const string Subject = "sub";
    public const string Name = "name";

    /// <summary>Random server-side session id; the cookie carries nothing else that matters.</summary>
    public const string SessionId = "bff_sid";
}

internal static class BffPolicies
{
    /// <summary>Authenticated BFF session (cookie scheme). Also the fallback policy: default deny.</summary>
    public const string Session = "bff-session";
}

/// <summary>
/// S1-05 BFF authentication (ADR-0012): OIDC authorization code + PKCE (S256) with form_post, confidential client,
/// `__Host-` HttpOnly Secure SameSite=Lax session cookie holding only subject, display name and session id;
/// tokens live in <see cref="BffSessionStore"/>. Unauthenticated calls get 401, never a redirect.
/// </summary>
internal static class BffAuthentication
{
    public const string SessionCookieName = "__Host-iam-session";
    public const string AntiforgeryCookieName = "__Host-iam-af";
    public const string AntiforgeryHeaderName = "X-XSRF-TOKEN";

    /// <summary>JS-readable cookie carrying the antiforgery request token for the SPA (Angular XSRF convention).</summary>
    public const string XsrfTokenCookieName = "__Host-XSRF-TOKEN";

    public const string IdTokenHintItemKey = "iam.bff.id_token_hint";

    public static IServiceCollection AddBffAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var oidc = configuration.GetSection(OidcSettings.SectionName).Get<OidcSettings>() ?? new OidcSettings();
        OidcEnvironmentGuard.EnsureValid(environment, oidc.Authority, oidc.RequireHttpsMetadata);
        if (string.IsNullOrWhiteSpace(oidc.ClientId))
        {
            throw new InvalidOperationException("Oidc:ClientId must be configured.");
        }

        if (string.IsNullOrWhiteSpace(oidc.ClientSecret))
        {
            throw new InvalidOperationException(
                "Oidc:ClientSecret is not configured. Locally: dotnet user-secrets set \"Oidc:ClientSecret\" <IAM_BFF_CLIENT_SECRET from .env> --project src/Host/IAM.Bff");
        }

        var session = configuration.GetSection(SessionSettings.SectionName).Get<SessionSettings>() ?? new SessionSettings();

        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = SessionCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax; // Strict would drop the cookie after the IdP's cross-site form_post
                options.Cookie.Path = "/";
                options.Cookie.IsEssential = true;
                options.ExpireTimeSpan = session.IdleTimeout;
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = static ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = static ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = ValidateSessionAsync;
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = oidc.Authority;
                options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.ClientSecret;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.FormPost; // keeps the code out of URLs and logs
                options.UsePkce = true;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.SaveTokens = false; // tokens go to the server-side session store, never into the cookie
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";
                options.SignedOutRedirectUri = "/";
                options.BackchannelTimeout = TimeSpan.FromSeconds(10);
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
                options.TokenValidationParameters.NameClaimType = BffClaimTypes.Name;
                options.Events.OnTokenValidated = OnTokenValidated;
                options.Events.OnRedirectToIdentityProviderForSignOut = static ctx =>
                {
                    if (ctx.HttpContext.Items.TryGetValue(IdTokenHintItemKey, out var hint) && hint is string idToken)
                    {
                        ctx.ProtocolMessage.IdTokenHint = idToken;
                    }

                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = static ctx =>
                {
                    // Generic response: IdP error details are not reflected to the browser.
                    ctx.HandleResponse();
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(BffPolicies.Session, SessionPolicy())
            .SetFallbackPolicy(SessionPolicy());

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.HeaderName = AntiforgeryHeaderName;
        });

        return services;
    }

    /// <summary>Builds the minimal cookie principal for a session. Used by sign-in and by tests.</summary>
    internal static ClaimsPrincipal CreateSessionPrincipal(string subject, string? name, string sessionId)
    {
        var claims = new List<Claim>
        {
            new(BffClaimTypes.Subject, subject),
            new(BffClaimTypes.SessionId, sessionId),
        };
        if (!string.IsNullOrEmpty(name))
        {
            claims.Add(new Claim(BffClaimTypes.Name, name));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, BffClaimTypes.Name, null));
    }

    private static AuthorizationPolicy SessionPolicy() =>
        new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(BffClaimTypes.SessionId)
            .Build();

    private static Task OnTokenValidated(TokenValidatedContext ctx)
    {
        var response = ctx.TokenEndpointResponse;
        var subject = ctx.Principal?.FindFirstValue(BffClaimTypes.Subject);
        if (response is null || string.IsNullOrEmpty(response.AccessToken) || string.IsNullOrEmpty(subject))
        {
            ctx.Fail("Token response incomplete.");
            return Task.CompletedTask;
        }

        var services = ctx.HttpContext.RequestServices;
        var time = services.GetRequiredService<TimeProvider>();
        var lifetime = int.TryParse(response.ExpiresIn, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 60;
        var tokens = new TokenSet(response.AccessToken, response.RefreshToken, response.IdToken, time.GetUtcNow().AddSeconds(lifetime));

        var session = services.GetRequiredService<BffSessionStore>().Create(subject, tokens);
        if (session is null)
        {
            ctx.Fail("Session capacity reached.");
            return Task.CompletedTask;
        }

        // Replace the IdP principal: keep subject and display name only (data minimisation).
        var name = ctx.Principal!.FindFirstValue("preferred_username") ?? ctx.Principal!.FindFirstValue(BffClaimTypes.Name);
        ctx.Principal = CreateSessionPrincipal(subject, name, session.Id);
        return Task.CompletedTask;
    }

    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext ctx)
    {
        var sessionId = ctx.Principal?.FindFirstValue(BffClaimTypes.SessionId);
        var store = ctx.HttpContext.RequestServices.GetRequiredService<BffSessionStore>();
        var session = store.Get(sessionId);
        if (session is null || !string.Equals(session.Subject, ctx.Principal?.FindFirstValue(BffClaimTypes.Subject), StringComparison.Ordinal))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        }
    }
}

/// <summary>Antiforgery for state-changing requests that carry the session cookie.</summary>
internal static class AntiforgeryMiddleware
{
    public static IApplicationBuilder UseBffAntiforgery(this IApplicationBuilder app) =>
        app.Use(static async (context, next) =>
        {
            if (RequiresAntiforgery(context) && context.User.Identity?.IsAuthenticated == true)
            {
                var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
                if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }

            await next(context).ConfigureAwait(false);
        });

    private static bool RequiresAntiforgery(HttpContext context)
    {
        var method = context.Request.Method;
        var unsafeMethod = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));
        var path = context.Request.Path;
        return unsafeMethod && (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/bff", StringComparison.OrdinalIgnoreCase));
    }
}
