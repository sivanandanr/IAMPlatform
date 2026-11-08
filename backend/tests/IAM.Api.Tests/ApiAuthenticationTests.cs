using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace IAM.Api.Tests;

/// <summary>S1-05: IAM.Api accepts only valid bearer tokens from the configured issuer/audience; default deny.</summary>
public sealed class ApiAuthenticationTests : IDisposable
{
    private const string SecuredPath = "/api/identity/me";
    private readonly ApiTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task LivenessIsAnonymous()
    {
        using var client = _host.CreateClient();
        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ValidTokenReachesSecuredEndpointAndOnlySubjectIsReturned()
    {
        using var response = await SendAsync(SecuredPath, _host.CreateValidToken());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body.Keys.ShouldBe(["subject"]);
        body["subject"].ShouldBe("subject-123");
    }

    [Fact]
    public async Task MissingTokenIsRejectedWithoutErrorDetails()
    {
        using var response = await SendAsync(SecuredPath, token: null);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldNotContain("error_description");
    }

    [Fact]
    public async Task UnknownRouteIsDeniedBeforeRoutingDetailsLeak()
    {
        using var response = await SendAsync("/api/does-not-exist", token: null);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WrongIssuerIsRejected()
    {
        var token = ApiTestHost.CreateToken(new SigningCredentials(_host.SigningKey, SecurityAlgorithms.RsaSha256), issuer: "https://evil.test/realms/test");
        using var response = await SendAsync(SecuredPath, token);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WrongAudienceIsRejected()
    {
        var token = ApiTestHost.CreateToken(new SigningCredentials(_host.SigningKey, SecurityAlgorithms.RsaSha256), audience: "account");
        using var response = await SendAsync(SecuredPath, token);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredTokenIsRejected()
    {
        var token = ApiTestHost.CreateToken(new SigningCredentials(_host.SigningKey, SecurityAlgorithms.RsaSha256), expires: DateTime.UtcNow.AddMinutes(-2));
        using var response = await SendAsync(SecuredPath, token);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnsignedAlgNoneTokenIsRejected()
    {
        var token = ApiTestHost.CreateToken(credentials: null);
        using var response = await SendAsync(SecuredPath, token);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SymmetricHs256TokenIsRejected()
    {
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        var token = ApiTestHost.CreateToken(new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        using var response = await SendAsync(SecuredPath, token);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenSignedByUnknownKeyIsRejected()
    {
        using var otherRsa = RSA.Create(2048);
        var token = ApiTestHost.CreateToken(new SigningCredentials(new RsaSecurityKey(otherRsa) { KeyId = "test-key-1" }, SecurityAlgorithms.RsaSha256));
        using var response = await SendAsync(SecuredPath, token);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenWithoutSubjectIsForbidden()
    {
        using var response = await SendAsync(SecuredPath, _host.CreateValidToken(subject: null));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ResponsesCarrySecurityHeaders()
    {
        using var response = await SendAsync("/health/live", token: null);
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string? token)
    {
        using var client = _host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
