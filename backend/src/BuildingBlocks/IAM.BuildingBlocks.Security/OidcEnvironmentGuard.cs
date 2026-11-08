using Microsoft.Extensions.Hosting;

namespace IAM.BuildingBlocks.Security;

/// <summary>
/// Startup guard for OIDC / JWT settings (ADR-0023, R-020). Outside the Development environment it refuses:
/// a missing or non-HTTPS authority, a loopback or local-test-realm authority, and disabled HTTPS metadata.
/// Hosts call <see cref="EnsureValid"/> before building the app so a misconfiguration fails closed at startup.
/// </summary>
public static class OidcEnvironmentGuard
{
    /// <summary>Realm name of the local Keycloak test provider (infra/docker/keycloak). Never trusted outside Development.</summary>
    public const string LocalTestRealmSegment = "/realms/iam-dev";

    public static IReadOnlyList<string> Validate(string environmentName, string? authority, bool requireHttpsMetadata)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(authority) || !Uri.TryCreate(authority, UriKind.Absolute, out var uri))
        {
            errors.Add("OIDC authority must be an absolute URI.");
            return errors;
        }

        var isDevelopment = string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);
        if (isDevelopment)
        {
            return errors;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"OIDC authority must use HTTPS outside Development (environment '{environmentName}').");
        }

        if (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"OIDC authority must not be a loopback/local address outside Development (environment '{environmentName}').");
        }

        if (uri.AbsolutePath.TrimEnd('/').EndsWith(LocalTestRealmSegment, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"The local test realm '{LocalTestRealmSegment}' must not be trusted outside Development (environment '{environmentName}').");
        }

        if (!requireHttpsMetadata)
        {
            errors.Add($"RequireHttpsMetadata must be true outside Development (environment '{environmentName}').");
        }

        return errors;
    }

    public static void EnsureValid(IHostEnvironment environment, string? authority, bool requireHttpsMetadata)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var errors = Validate(environment.EnvironmentName, authority, requireHttpsMetadata);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Refusing to start with unsafe OIDC settings: " + string.Join(" ", errors));
        }
    }
}
