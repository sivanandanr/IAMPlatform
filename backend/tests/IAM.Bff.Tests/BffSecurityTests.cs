using System.Net;
using System.Net.Http.Json;
using IAM.Bff.Authentication;
using IAM.Bff.Endpoints;
using Microsoft.AspNetCore.WebUtilities;
using Shouldly;

namespace IAM.Bff.Tests;

/// <summary>S1-05 BFF security behaviour (ADR-0012/0013). Abuse cases first; each has a positive counterpart.</summary>
public sealed class BffSecurityTests : IDisposable
{
    private readonly BffTestHost _host = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task LoginRedirectsToIdpWithPkceS256NonceStateAndFormPost()
    {
        using var client = _host.CreateClient();
        using var response = await client.GetAsync(new Uri("/bff/login?returnUrl=/requests", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).ShouldBe("https://idp.test/realms/test/auth");
        var query = QueryHelpers.ParseQuery(location.Query);
        query["response_type"].ToString().ShouldBe("code");
        query["response_mode"].ToString().ShouldBe("form_post");
        query["code_challenge_method"].ToString().ShouldBe("S256");
        query["code_challenge"].ToString().ShouldNotBeNullOrEmpty();
        query["nonce"].ToString().ShouldNotBeNullOrEmpty();
        query["state"].ToString().ShouldNotBeNullOrEmpty();
        query["client_id"].ToString().ShouldBe("iam-bff");
        query.ShouldNotContainKey("client_secret");
        query["scope"].ToString().ShouldBe("openid profile");
    }

    [Theory]
    [InlineData("https://evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("requests")]
    public async Task LoginRejectsNonLocalReturnUrls(string returnUrl)
    {
        using var client = _host.CreateClient();
        using var response = await client.GetAsync(new Uri("/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl), UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/requests?id=1", true)]
    [InlineData("//evil", false)]
    [InlineData("/\\evil", false)]
    [InlineData("/x\r\nSet-Cookie:a=b", false)]
    [InlineData("", false)]
    public void ReturnUrlValidation(string url, bool expected) => BffEndpoints.IsLocalReturnUrl(url).ShouldBe(expected);

    [Theory]
    [InlineData("/bff/user")]
    [InlineData("/api/identity/me")]
    [InlineData("/anything-else")]
    public async Task AnonymousRequestsGet401NotARedirectAndAreNotForwarded(string path)
    {
        using var client = _host.CreateClient();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
        _host.Backend.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task SessionCookieIsHostPrefixedSecureHttpOnlyLax()
    {
        using var client = _host.CreateClient();
        using var response = await client.PostAsync(new Uri(BffTestHost.TestSignInPath, UriKind.Relative), null, Ct);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(BffAuthentication.SessionCookieName + "=", StringComparison.Ordinal));
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=lax", Case.Insensitive);
        cookie.ShouldContain("path=/", Case.Insensitive);
        cookie.ShouldNotContain("domain=", Case.Insensitive);
    }

    [Fact]
    public async Task UserEndpointReturnsSubjectOnlyAndNoTokens()
    {
        using var client = _host.CreateClient();
        await BffTestHost.SignInAsync(client);
        using var response = await client.GetAsync(new Uri("/bff/user", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("subject-123");
        body.ShouldNotContain("access-1");
        body.ShouldNotContain("refresh-1");
        body.ShouldNotContain("id-1");
        response.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith(BffAuthentication.XsrfTokenCookieName + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProxyAttachesSessionTokenAndStripsSpoofedHeaders()
    {
        using var client = _host.CreateClient();
        await BffTestHost.SignInAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/identity/me", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer attacker-token");
        request.Headers.TryAddWithoutValidation("X-User-Id", "admin");
        request.Headers.TryAddWithoutValidation("X-Roles", "SecurityAdmin");
        request.Headers.TryAddWithoutValidation("X-Original-URL", "/admin");
        request.Headers.TryAddWithoutValidation("Forwarded", "for=6.6.6.6");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "6.6.6.6");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _host.Backend.Requests.TryDequeue(out var forwarded).ShouldBeTrue();
        forwarded!["Authorization"].ShouldBe("Bearer access-1");
        forwarded.ShouldNotContainKey("Cookie");
        forwarded.ShouldNotContainKey("X-User-Id");
        forwarded.ShouldNotContainKey("X-Roles");
        forwarded.ShouldNotContainKey("X-Original-URL");
        forwarded.ShouldNotContainKey("Forwarded");
        forwarded.GetValueOrDefault("X-Forwarded-For", string.Empty).ShouldNotContain("6.6.6.6");
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task UnsafeApiCallWithoutAntiforgeryTokenIsRejectedAndNotForwarded()
    {
        using var client = _host.CreateClient();
        await BffTestHost.SignInAsync(client);
        using var response = await client.PostAsJsonAsync(new Uri("/api/requests", UriKind.Relative), new { x = 1 }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _host.Backend.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task UnsafeApiCallWithAntiforgeryTokenIsForwarded()
    {
        using var client = _host.CreateClient();
        await BffTestHost.SignInAsync(client);
        var xsrf = await GetXsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/requests", UriKind.Relative)) { Content = JsonContent.Create(new { x = 1 }) };
        request.Headers.Add(BffAuthentication.AntiforgeryHeaderName, xsrf);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _host.Backend.Calls.ShouldBe(1);
        _host.Backend.Requests.TryDequeue(out var forwarded).ShouldBeTrue();
        forwarded!.ShouldNotContainKey(BffAuthentication.AntiforgeryHeaderName);
    }

    [Fact]
    public async Task RevokedServerSideSessionInvalidatesTheCookie()
    {
        using var client = _host.CreateClient();
        var sessionId = await BffTestHost.SignInAsync(client);
        _host.Sessions.Remove(sessionId);

        using var response = await client.GetAsync(new Uri("/bff/user", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredAccessTokenIsRefreshedOnceForConcurrentRequests()
    {
        using var client = _host.CreateClient();
        await BffTestHost.SignInAsync(client, accessTokenExpired: true);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => client.GetAsync(new Uri("/api/identity/me", UriKind.Relative), Ct)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        _host.Refresher.Calls.ShouldBe(1);
        _host.Backend.Requests.ShouldAllBe(h => h["Authorization"] == "Bearer access-2");
        foreach (var r in responses)
        {
            r.Dispose();
        }
    }

    [Fact]
    public async Task FailedRefreshEndsTheSessionAndNothingIsForwarded()
    {
        _host.Refresher.Result = null;
        using var client = _host.CreateClient();
        var sessionId = await BffTestHost.SignInAsync(client, accessTokenExpired: true);

        using var response = await client.GetAsync(new Uri("/api/identity/me", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _host.Backend.Calls.ShouldBe(0);
        _host.Sessions.Get(sessionId).ShouldBeNull();
    }

    [Fact]
    public async Task LogoutRequiresPost()
    {
        using var client = _host.CreateClient();
        await BffTestHost.SignInAsync(client);
        using var response = await client.GetAsync(new Uri("/bff/logout", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task LogoutWithoutAntiforgeryTokenIsRejectedAndSessionSurvives()
    {
        using var client = _host.CreateClient();
        var sessionId = await BffTestHost.SignInAsync(client);
        using var response = await client.PostAsync(new Uri("/bff/logout", UriKind.Relative), null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _host.Sessions.Get(sessionId).ShouldNotBeNull();
    }

    [Fact]
    public async Task LogoutRevokesSessionAndEndsIdpSessionWithIdTokenHint()
    {
        using var client = _host.CreateClient();
        var sessionId = await BffTestHost.SignInAsync(client);
        var xsrf = await GetXsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/bff/logout", UriKind.Relative));
        request.Headers.Add(BffAuthentication.AntiforgeryHeaderName, xsrf);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.GetLeftPart(UriPartial.Path).ShouldBe("https://idp.test/realms/test/logout");
        QueryHelpers.ParseQuery(response.Headers.Location.Query)["id_token_hint"].ToString().ShouldBe("id-1");
        _host.Sessions.Get(sessionId).ShouldBeNull();

        using var after = await client.GetAsync(new Uri("/bff/user", UriKind.Relative), Ct);
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResponsesCarrySecurityHeaders()
    {
        using var client = _host.CreateClient();
        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors 'none'");
    }

    private static async Task<string> GetXsrfTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/bff/user", UriKind.Relative), Ct);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(BffAuthentication.XsrfTokenCookieName + "=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(cookie[(BffAuthentication.XsrfTokenCookieName.Length + 1)..].Split(';')[0]);
    }
}

/// <summary>Startup guards (R-020, ADR-0023) and fail-closed configuration.</summary>
public sealed class BffStartupGuardTests
{
    [Fact]
    public void RefusesLocalTestIssuerOutsideDevelopment()
    {
        using var host = new BffTestHost(environment: "Production", authority: "http://localhost:8180/realms/iam-dev", requireHttpsMetadata: false);
        Flatten(Should.Throw<Exception>(() => host.CreateClient()))
            .ShouldContain(e => e is InvalidOperationException && e.Message.Contains("unsafe OIDC settings", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesToStartWithoutClientSecret()
    {
        using var host = new BffTestHost(clientSecret: null);
        Flatten(Should.Throw<Exception>(() => host.CreateClient()))
            .ShouldContain(e => e is InvalidOperationException && e.Message.Contains("Oidc:ClientSecret", StringComparison.Ordinal));
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }
}
