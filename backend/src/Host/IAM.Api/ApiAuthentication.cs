using IAM.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace IAM.Api;

/// <summary>Bound from configuration section <c>Authentication:Jwt</c>. Contains no secrets.</summary>
internal sealed class JwtSettings
{
    public const string SectionName = "Authentication:Jwt";

    /// <summary>Exact issuer URL; also used for metadata discovery and as the only valid issuer.</summary>
    public string Authority { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public bool RequireHttpsMetadata { get; set; } = true;
}

/// <summary>
/// S1-05: the API accepts only bearer access tokens forwarded by the BFF (ADR-0012/0013).
/// Validation: exact issuer, audience, RS256 signature from the issuer's keys, lifetime; default-deny authorization.
/// </summary>
internal static class ApiAuthentication
{
    public static IServiceCollection AddIamApiAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();
        OidcEnvironmentGuard.EnsureValid(environment, settings.Authority, settings.RequireHttpsMetadata);
        if (string.IsNullOrWhiteSpace(settings.Audience))
        {
            throw new InvalidOperationException($"{JwtSettings.SectionName}:Audience must be configured.");
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = settings.Authority;
                options.Audience = settings.Audience;
                options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Authority,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                };
            });

        // Default deny: every endpoint requires an authenticated caller unless it explicitly opts out. The default policy
        // (which RequirePermission combines with) is the same, so it never depends on which scheme happens to be default.
        var authenticated = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build();
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(authenticated)
            .SetFallbackPolicy(authenticated);

        return services;
    }
}
