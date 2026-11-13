using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IAM.BuildingBlocks.Security;

/// <summary>
/// Rate limits (BACKEND_INPUT_VALIDATION, DL-063c). The host runs the limiter after authentication and before
/// authorization, so a flood is stopped before any permission check reads the access model:
/// <list type="bullet">
/// <item>every request: per person (<c>sub</c>) when signed in, otherwise per client address;</item>
/// <item>access-control changes: a stricter per-person limit on top.</item>
/// </list>
/// Limits are per process: with several API instances the effective limit multiplies (R-033) until a shared store is used.
/// </summary>
public static class IamRateLimits
{
    /// <summary>Access-control changes (propose, approve, reject, withdraw).</summary>
    public const string AdministrationWrites = "iam-administration-writes";

    public const string AdministrationWritesPerMinuteKey = "RateLimiting:AdministrationWritesPerMinute";
    public const string RequestsPerMinutePerPersonKey = "RateLimiting:RequestsPerMinutePerPerson";
    public const string AnonymousRequestsPerMinutePerAddressKey = "RateLimiting:AnonymousRequestsPerMinutePerAddress";

    public const int DefaultAdministrationWritesPerMinute = 30;
    public const int DefaultRequestsPerMinutePerPerson = 300;
    public const int DefaultAnonymousRequestsPerMinutePerAddress = 120;

    public static IServiceCollection AddIamRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var writes = Read(configuration, AdministrationWritesPerMinuteKey, DefaultAdministrationWritesPerMinute);
        var perPerson = Read(configuration, RequestsPerMinutePerPersonKey, DefaultRequestsPerMinutePerPerson);
        var perAddress = Read(configuration, AnonymousRequestsPerMinutePerAddressKey, DefaultAnonymousRequestsPerMinutePerAddress);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                var subject = http.User.Identity?.IsAuthenticated == true ? http.User.FindFirst("sub")?.Value : null;
                return string.IsNullOrEmpty(subject)
                    ? Window("address:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"), perAddress)
                    : Window("person:" + subject, perPerson);
            });
            options.AddPolicy(AdministrationWrites, http => Window(http.User.FindFirst("sub")?.Value ?? string.Empty, writes));
        });
        return services;
    }

    private static RateLimitPartition<string> Window(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });

    private static int Read(IConfiguration configuration, string key, int fallback)
    {
        var value = configuration.GetValue<int?>(key) ?? fallback;
        return value is < 1 or > 100_000 ? throw new InvalidOperationException($"{key} must be between 1 and 100000.") : value;
    }
}
