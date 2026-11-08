using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace IAM.Bff.Sessions;

/// <summary>Exchanges a refresh token for a new token set. Returns null on any failure (fail closed).</summary>
internal interface ITokenRefresher
{
    Task<TokenSet?> RefreshAsync(TokenSet current, CancellationToken cancellationToken);
}

/// <summary>Returns a valid access token for a session, refreshing it server-side when close to expiry.</summary>
internal sealed partial class SessionTokenService(
    BffSessionStore store,
    ITokenRefresher refresher,
    TimeProvider timeProvider,
    IOptions<SessionSettings> options,
    ILogger<SessionTokenService> logger)
{
    private readonly TimeSpan _refreshBeforeExpiry = options.Value.RefreshBeforeExpiry;

    public async Task<string?> GetAccessTokenAsync(BffSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (IsFresh(session.Tokens))
        {
            return session.Tokens.AccessToken;
        }

        try
        {
            await session.RefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return null; // session was removed concurrently (logout/expiry)
        }

        try
        {
            // Another request may have refreshed while we waited.
            if (IsFresh(session.Tokens))
            {
                return session.Tokens.AccessToken;
            }

            var refreshed = string.IsNullOrEmpty(session.Tokens.RefreshToken)
                ? null
                : await refresher.RefreshAsync(session.Tokens, cancellationToken).ConfigureAwait(false);

            if (refreshed is null)
            {
                LogRefreshFailed(logger);
                store.Remove(session.Id);
                return null;
            }

            session.Tokens = refreshed;
            return refreshed.AccessToken;
        }
        finally
        {
            try
            {
                session.RefreshLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // session removed while refreshing; nothing to release
            }
        }
    }

    private bool IsFresh(TokenSet tokens) => tokens.AccessTokenExpiresAt - timeProvider.GetUtcNow() > _refreshBeforeExpiry;

    [LoggerMessage(EventId = 5101, Level = LogLevel.Information, Message = "Access token refresh failed or unavailable; BFF session ended.")]
    private static partial void LogRefreshFailed(ILogger logger);
}

/// <summary>Refresh-token grant against the configured IdP token endpoint (client_secret_post).</summary>
internal sealed partial class OidcTokenRefresher(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    TimeProvider timeProvider,
    ILogger<OidcTokenRefresher> logger) : ITokenRefresher
{
    public const string HttpClientName = "oidc-token";

    public async Task<TokenSet?> RefreshAsync(TokenSet current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        var options = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        if (options.ConfigurationManager is null || string.IsNullOrEmpty(current.RefreshToken))
        {
            return null;
        }

        try
        {
            var configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            if (!IsSameOrigin(configuration.TokenEndpoint, options.Authority))
            {
                LogTokenEndpointRejected(logger);
                return null; // SSRF guard: only the configured authority's origin
            }

            using var content = new FormUrlEncodedContent(
            [
                new("grant_type", "refresh_token"),
                new("refresh_token", current.RefreshToken),
                new("client_id", options.ClientId ?? string.Empty),
                new("client_secret", options.ClientSecret ?? string.Empty),
            ]);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(configuration.TokenEndpoint)) { Content = content };
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogRefreshRejected(logger, (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<TokenEndpointResponse>(cancellationToken).ConfigureAwait(false);
            if (body is null || string.IsNullOrEmpty(body.AccessToken) || body.ExpiresIn <= 0)
            {
                return null;
            }

            return new TokenSet(
                body.AccessToken,
                body.RefreshToken ?? current.RefreshToken,
                body.IdToken ?? current.IdToken,
                timeProvider.GetUtcNow().AddSeconds(body.ExpiresIn));
        }
        catch (HttpRequestException)
        {
            LogRefreshError(logger, "transport");
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogRefreshError(logger, "timeout");
            return null;
        }
        catch (JsonException)
        {
            LogRefreshError(logger, "invalid response");
            return null;
        }
    }

    internal static bool IsSameOrigin(string? endpoint, string? authority) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var e)
        && Uri.TryCreate(authority, UriKind.Absolute, out var a)
        && string.Equals(e.Scheme, a.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(e.Host, a.Host, StringComparison.OrdinalIgnoreCase)
        && e.Port == a.Port;

    [LoggerMessage(EventId = 5102, Level = LogLevel.Warning, Message = "Token endpoint refused the refresh request with status {StatusCode}.")]
    private static partial void LogRefreshRejected(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Warning, Message = "Token refresh failed: {Reason}.")]
    private static partial void LogRefreshError(ILogger logger, string reason);

    [LoggerMessage(EventId = 5104, Level = LogLevel.Error, Message = "Token endpoint is not on the configured authority's origin; refresh refused.")]
    private static partial void LogTokenEndpointRejected(ILogger logger);

    private sealed record TokenEndpointResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

/// <summary>Registration helpers kept next to the types (keeps Program.cs declarative).</summary>
internal static class SessionServiceCollectionExtensions
{
    public static IServiceCollection AddBffSessions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SessionSettings>().Bind(configuration.GetSection(SessionSettings.SectionName))
            .Validate(s => s.IdleTimeout > TimeSpan.Zero && s.AbsoluteTimeout >= s.IdleTimeout && s.MaxSessions > 0, "Invalid Session settings.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new BffSessionStore(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<SessionSettings>>()));
        services.AddSingleton<ITokenRefresher>(sp => new OidcTokenRefresher(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<OidcTokenRefresher>>()));
        services.AddSingleton(sp => new SessionTokenService(
            sp.GetRequiredService<BffSessionStore>(),
            sp.GetRequiredService<ITokenRefresher>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<SessionSettings>>(),
            sp.GetRequiredService<ILogger<SessionTokenService>>()));
        services.AddHttpClient(OidcTokenRefresher.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.MaxResponseContentBufferSize = 64 * 1024;
        });
        return services;
    }
}
