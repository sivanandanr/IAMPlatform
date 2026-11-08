using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace IAM.Api.Tests;

/// <summary>
/// In-memory IAM.Api with a test issuer. Signing keys are generated per run; no network, no real IdP.
/// Tokens are minted here only to exercise validation — this is test code, not an identity provider.
/// </summary>
public sealed class ApiTestHost : IDisposable
{
    public const string Issuer = "https://idp.test/realms/test";
    public const string Audience = "iam-api";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTestHost(string environment = "Testing", string authority = Issuer, bool requireHttpsMetadata = true)
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-key-1" };
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Authentication:Jwt:Authority", authority);
            builder.UseSetting("Authentication:Jwt:Audience", Audience);
            builder.UseSetting("Authentication:Jwt:RequireHttpsMetadata", requireHttpsMetadata ? "true" : "false");
            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(SigningKey);
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                }));
        });
    }

    public RsaSecurityKey SigningKey { get; }

    public HttpClient CreateClient() => _factory.CreateClient();

    public static string CreateToken(
        SigningCredentials? credentials,
        string issuer = Issuer,
        string audience = Audience,
        string? subject = "subject-123",
        DateTime? expires = null)
    {
        var now = DateTime.UtcNow;
        var exp = expires ?? now.AddMinutes(5);
        var claims = new List<Claim>();
        if (subject is not null)
        {
            claims.Add(new Claim("sub", subject));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = exp.AddMinutes(-10),
            NotBefore = exp.AddMinutes(-10),
            Expires = exp,
            SigningCredentials = credentials,
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public string CreateValidToken(string? subject = "subject-123") =>
        CreateToken(new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256), subject: subject);

    public void Dispose()
    {
        _factory.Dispose();
        _rsa.Dispose();
    }
}
