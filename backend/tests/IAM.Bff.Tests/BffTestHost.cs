using System.Collections.Concurrent;
using System.Net;
using IAM.Bff.Authentication;
using IAM.Bff.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Shouldly;
using Yarp.ReverseProxy.Forwarder;

namespace IAM.Bff.Tests;

/// <summary>
/// In-memory IAM.Bff: static IdP metadata (no network), fake token refresher, echo backend instead of IAM.Api,
/// and a test-only sign-in middleware that creates a server-side session the same way the OIDC callback does.
/// </summary>
internal sealed class BffTestHost : IDisposable
{
    public const string Issuer = "https://idp.test/realms/test";
    public const string TestSignInPath = "/__test/signin";

    private readonly WebApplicationFactory<Program> _factory;

    public BffTestHost(string environment = "Testing", string authority = Issuer, string? clientSecret = "test-only-not-a-secret", bool requireHttpsMetadata = true)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Oidc:Authority", authority);
            builder.UseSetting("Oidc:ClientSecret", clientSecret ?? string.Empty);
            builder.UseSetting("Oidc:RequireHttpsMetadata", requireHttpsMetadata ? "true" : "false");
            builder.UseSetting("ReverseProxy:Clusters:iam-api:Destinations:test:Address", "http://backend.test/");
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = Issuer,
                        AuthorizationEndpoint = "https://idp.test/realms/test/auth",
                        TokenEndpoint = "https://idp.test/realms/test/token",
                        EndSessionEndpoint = "https://idp.test/realms/test/logout",
                    };
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
                services.RemoveAll<ITokenRefresher>();
                services.AddSingleton<ITokenRefresher>(Refresher);
                services.RemoveAll<IForwarderHttpClientFactory>();
                services.AddSingleton<IForwarderHttpClientFactory>(new EchoForwarderFactory(Backend));
                services.AddSingleton<IStartupFilter>(new TestSignInStartupFilter());
            });
        });
    }

    public EchoBackend Backend { get; } = new();

    public FakeTokenRefresher Refresher { get; } = new();

    public BffSessionStore Sessions => _factory.Services.GetRequiredService<BffSessionStore>();

    public HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>Signs the client in; returns the server-side session id.</summary>
    public static async Task<string> SignInAsync(HttpClient client, bool accessTokenExpired = false)
    {
        var path = TestSignInPath + (accessTokenExpired ? "?expired=1" : string.Empty);
        using var response = await client.PostAsync(new Uri(path, UriKind.Relative), null, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    public void Dispose() => _factory.Dispose();

    private sealed class TestSignInStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (!context.Request.Path.Equals(TestSignInPath, StringComparison.Ordinal))
                {
                    await nextMiddleware(context);
                    return;
                }

                var expired = context.Request.Query.ContainsKey("expired");
                var tokens = new TokenSet("access-1", "refresh-1", "id-1", DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 5));
                var session = context.RequestServices.GetRequiredService<BffSessionStore>().Create("subject-123", tokens)!;
                await context.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    BffAuthentication.CreateSessionPrincipal("subject-123", "dev.requester", session.Id));
                await context.Response.WriteAsync(session.Id);
            });
            next(app);
        };
    }
}

/// <summary>Stands in for IAM.Api: records what the BFF forwarded.</summary>
internal sealed class EchoBackend : HttpMessageHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public ConcurrentQueue<Dictionary<string, string>> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Requests.Enqueue(request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.Headers.Add("Set-Cookie", "backend=should-not-reach-browser; Path=/");
        return Task.FromResult(response);
    }
}

internal sealed class EchoForwarderFactory(EchoBackend backend) : IForwarderHttpClientFactory
{
    public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(backend, disposeHandler: false);
}

/// <summary>Configurable refresher; counts calls to prove refreshes are serialised per session.</summary>
internal sealed class FakeTokenRefresher : ITokenRefresher
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public TokenSet? Result { get; set; } = new("access-2", "refresh-2", "id-1", DateTimeOffset.UtcNow.AddMinutes(5));

    public async Task<TokenSet?> RefreshAsync(TokenSet current, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        await Task.Delay(50, cancellationToken);
        return Result;
    }
}
