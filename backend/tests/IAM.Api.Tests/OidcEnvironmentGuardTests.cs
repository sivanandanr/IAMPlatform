using IAM.BuildingBlocks.Security;
using Shouldly;

namespace IAM.Api.Tests;

/// <summary>R-020 / ADR-0023: the local test issuer and insecure metadata are refused outside Development.</summary>
public sealed class OidcEnvironmentGuardTests
{
    private const string LocalIssuer = "http://localhost:8180/realms/iam-dev";

    [Fact]
    public void DevelopmentAcceptsLocalHttpIssuer() =>
        OidcEnvironmentGuard.Validate("Development", LocalIssuer, requireHttpsMetadata: false).ShouldBeEmpty();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void NonDevelopmentRejectsLocalTestIssuer(string environment)
    {
        var errors = OidcEnvironmentGuard.Validate(environment, LocalIssuer, requireHttpsMetadata: false);
        errors.ShouldContain(e => e.Contains("HTTPS", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("loopback", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("/realms/iam-dev", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("RequireHttpsMetadata", StringComparison.Ordinal));
    }

    [Fact]
    public void NonDevelopmentRejectsTestRealmEvenOnHttpsHost() =>
        OidcEnvironmentGuard.Validate("Production", "https://idp.example.com/realms/iam-dev/", requireHttpsMetadata: true)
            .ShouldHaveSingleItem().ShouldContain("/realms/iam-dev");

    [Theory]
    [InlineData("https://127.0.0.1/realms/prod")]
    [InlineData("https://localhost/realms/prod")]
    public void NonDevelopmentRejectsLoopback(string authority) =>
        OidcEnvironmentGuard.Validate("Production", authority, requireHttpsMetadata: true).ShouldNotBeEmpty();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uri")]
    public void EveryEnvironmentRejectsMissingAuthority(string? authority)
    {
        OidcEnvironmentGuard.Validate("Development", authority, requireHttpsMetadata: true).ShouldNotBeEmpty();
        OidcEnvironmentGuard.Validate("Production", authority, requireHttpsMetadata: true).ShouldNotBeEmpty();
    }

    [Fact]
    public void ProductionAcceptsHttpsEnterpriseIssuer() =>
        OidcEnvironmentGuard.Validate("Production", "https://login.example.com/tenant/v2.0", requireHttpsMetadata: true).ShouldBeEmpty();

    [Fact]
    public void ApiRefusesToStartInProductionWithLocalIssuer()
    {
        using var host = new ApiTestHost(environment: "Production", authority: LocalIssuer, requireHttpsMetadata: false);
        var ex = Should.Throw<Exception>(() => host.CreateClient());
        Flatten(ex).ShouldContain(e => e is InvalidOperationException && e.Message.Contains("unsafe OIDC settings", StringComparison.Ordinal));
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }
}
