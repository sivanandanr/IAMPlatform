using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using static IAM.Persistence.Tests.Parity.AccessControlParityTests;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// P0-013 slice 4: the admin API end to end on SQL Server and PostgreSQL, through the real host, token validation,
/// permission checks, validation and storage. Covers the mandatory negative list (BACKEND_INPUT_VALIDATION): mass
/// assignment, duplicate keys, deep JSON, content type, oversize, unknown kinds, self-grant, self-approval, replay,
/// another person's request (BOLA), missing permission and stored HTML/SQL text.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class AccessApiParityTests(ParityDatabase db) : IDisposable
{
    private const string Changes = "/api/administration/changes";
    private readonly ApiParityHost _host = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task ProposeReadApproveAndThePersonSeesTheirNewRole()
    {
        await BootstrapAsync();
        var subject = NewSubject("api-flow");
        using var proposed = await PostAsync(Administrator, Changes, new { kind = "AssignRole", subject, roleCode = "auditor", reason = "Quarterly cover" });
        proposed.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await ReadAsync(proposed);
        var id = created.GetProperty("id").GetGuid();
        proposed.Headers.Location!.ToString().ShouldBe($"{Changes}/{id}");
        created.GetProperty("maker").GetString().ShouldBe(Administrator, "the maker comes from the token, never the body");
        created.GetProperty("state").GetString().ShouldBe("Proposed");

        using (var read = await GetAsync(Manager, $"{Changes}/{id}"))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await ReadAsync(read)).GetProperty("change").GetProperty("subject").GetString().ShouldBe(subject);
        }

        using (var approved = await PostAsync(Manager, $"{Changes}/{id}/approve", new { reason = "Checked" }))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await ReadAsync(approved);
            body.GetProperty("state").GetString().ShouldBe("Approved");
            body.GetProperty("checker").GetString().ShouldBe(Manager);
        }

        using var mine = await GetAsync(subject, "/api/administration/permissions/me");
        mine.StatusCode.ShouldBe(HttpStatusCode.OK);
        var roles = (await ReadAsync(mine)).GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        roles.ShouldBe(["auditor"]);
    }

    [Fact]
    public async Task RolesCanBeReadByThoseAllowedAndNobodyElse()
    {
        await BootstrapAsync();
        using (var allowed = await GetAsync(Manager, "/api/administration/roles"))
        {
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await ReadAsync(allowed);
            body.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("code").GetString()).ShouldContain("iam-administrator");
            (await allowed.Content.ReadAsStringAsync(Ct)).ShouldNotContain(Administrator, Case.Sensitive, "roles, not who holds them");
        }

        using var denied = await GetAsync(NewSubject("nobody"), "/api/administration/roles");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DenialsAreAuditedOncePerPersonAndActionPerMinute()
    {
        await BootstrapAsync();
        var outsider = NewSubject("denied");
        for (var i = 0; i < 3; i++)
        {
            using var denied = await GetAsync(outsider, "/api/administration/roles");
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var sql = db.Provider == DatabaseProvider.SqlServer
            ? $"SELECT COUNT(*) FROM [audit].[AuditEvent] WHERE [Action] = 'administration.authorization.denied' AND [ActorId] = '{outsider}' AND [TargetId] = 'administration.roles.read' AND [Outcome] = 'Denied'"
            : $"SELECT COUNT(*) FROM audit.audit_event WHERE action = 'administration.authorization.denied' AND actor_id = '{outsider}' AND target_id = 'administration.roles.read' AND outcome = 'Denied'";
        Convert.ToInt64(await db.ScalarAsync(db.AuditAppConnectionString, sql, Ct), CultureInfo.InvariantCulture).ShouldBe(1);
    }

    [Theory]
    [InlineData("""{"kind":"AssignRole","subject":"x","roleCode":"auditor","reason":"r","maker":"access-manager"}""")]
    [InlineData("""{"kind":"AssignRole","subject":"x","roleCode":"auditor","reason":"r","isAdmin":true}""")]
    [InlineData("""{"kind":"AssignRole","subject":"x","roleCode":"auditor","reason":"r","state":"Approved"}""")]
    [InlineData("""{"kind":"AssignRole","subject":"x","subject":"y","roleCode":"auditor","reason":"r"}""")]
    [InlineData("""{"kind":"GrantEverything","reason":"r"}""")]
    [InlineData("""{"kind":"assignrole","subject":"x","roleCode":"auditor","reason":"r"}""")]
    [InlineData("""{"kind":"AssignRole","subject":"x","roleCode":"auditor"}""")]
    [InlineData("""{"kind":"AssignRole","subject":"x","roleCode":"auditor","reason":"r","actions":["administration.changes.approve"]}""")]
    [InlineData("""{"kind":"AssignRole","subject":"x","roleCode":"Auditor';--","reason":"r"}""")]
    [InlineData("""{"kind":"AssignRole","subject":123,"roleCode":"auditor","reason":"r"}""")]
    [InlineData("""not json""")]
    [InlineData("""null""")]
    public async Task MalformedOrOverreachingBodiesAre400AndStoreNothing(string json)
    {
        await BootstrapAsync();
        var before = await CountChangesAsync();
        using var response = await SendRawAsync(Administrator, Changes, json, "application/json");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CountChangesAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task DeeplyNestedJsonIsRefused()
    {
        await BootstrapAsync();
        var nested = string.Concat(Enumerable.Repeat("[", 64)) + string.Concat(Enumerable.Repeat("]", 64));
        using var response = await SendRawAsync(Administrator, Changes, $$"""{"kind":"DefineRole","code":"deep","name":"Deep","actions":{{nested}},"reason":"r"}""", "application/json");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WrongContentTypeIs415()
    {
        await BootstrapAsync();
        using var response = await SendRawAsync(Administrator, Changes, """{"kind":"AssignRole","subject":"x","roleCode":"auditor","reason":"r"}""", "text/plain");
        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task OversizedBodiesAre413()
    {
        await BootstrapAsync();
        var json = $$"""{"kind":"AssignRole","subject":"x","roleCode":"auditor","reason":"{{new string('r', 20_000)}}"}""";
        using var response = await SendRawAsync(Administrator, Changes, json, "application/json");
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task SelfGrantIs403WithOnlyTheRefusalCode()
    {
        await BootstrapAsync();
        using var response = await PostAsync(Administrator, Changes, new { kind = "AssignRole", subject = Administrator, roleCode = "auditor", reason = "Mine" });
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await ReadAsync(response);
        body.GetProperty("code").GetString().ShouldBe("SelfGrant");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("Mine");
    }

    [Fact]
    public async Task OnlyTheRightPeopleCanActAndAReplayIsAConflict()
    {
        await BootstrapAsync();
        var id = await ProposeAsync(NewSubject("rights"));

        // Someone without the permission is stopped before the handler.
        using (var outsider = await PostAsync(NewSubject("outsider"), $"{Changes}/{id}/approve", new { reason = (string?)null }))
        {
            outsider.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // The maker can't approve or reject: an administrator holds read and propose, not approve, so the permission check
        // stops them before the handler (no body, unlike a domain refusal, which carries a code).
        foreach (var decision in new[] { "approve", "reject" })
        {
            using var maker = await PostAsync(Administrator, $"{Changes}/{id}/{decision}", new { reason = "Mine" });
            maker.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await maker.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        }

        // A manager holds approve, not propose: stopped at the permission check too.
        using (var proposer = await PostAsync(Manager, Changes, new { kind = "AssignRole", subject = NewSubject("mgr"), roleCode = "auditor", reason = "r" }))
        {
            proposer.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await proposer.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        }

        // Another administrator can't withdraw someone else's request (BOLA).
        using (var other = await PostAsync(SecondAdministrator, $"{Changes}/{id}/withdraw", new { reason = "Not mine" }))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ReadAsync(other)).GetProperty("code").GetString().ShouldBe("NotMaker");
        }

        // Rejecting needs a reason.
        using (var bare = await PostAsync(Manager, $"{Changes}/{id}/reject", new { reason = (string?)null }))
        {
            bare.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var approve = await PostAsync(Manager, $"{Changes}/{id}/approve", new { reason = (string?)null }))
        {
            approve.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var replay = await PostAsync(SecondManager, $"{Changes}/{id}/approve", new { reason = (string?)null });
        replay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadAsync(replay)).GetProperty("code").GetString().ShouldBe("NotProposed");
    }

    [Fact]
    public async Task UnknownRequestsAre404AndReadingNeedsThePermission()
    {
        await BootstrapAsync();
        using (var missing = await GetAsync(Manager, $"{Changes}/{Guid.CreateVersion7()}"))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var missingDecision = await PostAsync(Manager, $"{Changes}/{Guid.CreateVersion7()}/approve", new { reason = (string?)null }))
        {
            missingDecision.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        var id = await ProposeAsync(NewSubject("hidden"));
        using var denied = await GetAsync(NewSubject("curious"), $"{Changes}/{id}");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task HtmlAndSqlTextAreStoredAsPlainDataAndReturnedEncoded()
    {
        await BootstrapAsync();
        var subject = "o'brien'; DROP TABLE configuration.access_role;--" + Guid.NewGuid().ToString("N")[..6];
        const string reason = "<script>alert(1)</script> & \"quotes\"";
        using var response = await PostAsync(Administrator, Changes, new { kind = "AssignRole", subject, roleCode = "auditor", reason });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        raw.ShouldNotContain("<script>");
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("reason").GetString().ShouldBe(reason);
        body.GetProperty("change").GetProperty("subject").GetString().ShouldBe(subject);

        using var roles = await GetAsync(Manager, "/api/administration/roles");
        roles.StatusCode.ShouldBe(HttpStatusCode.OK, "the tables are still there");
    }

    [Fact]
    public async Task WritesAreRateLimitedPerPerson()
    {
        await BootstrapAsync();
        using var limited = new ApiParityHost(db, writesPerMinute: 2);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await limited.SendAsync(Administrator, HttpMethod.Post, Changes, Json(new { kind = "AssignRole", subject = NewSubject("limit"), roleCode = "auditor", reason = "r" }));
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests]);

        // Another person has their own allowance.
        using var other = await limited.SendAsync(SecondAdministrator, HttpMethod.Post, Changes, Json(new { kind = "AssignRole", subject = NewSubject("limit"), roleCode = "auditor", reason = "r" }));
        other.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private Task BootstrapAsync() => new AccessControlParityTests(db).EnsureAccessBootstrappedAsync();

    private async Task<Guid> ProposeAsync(string subject)
    {
        using var response = await PostAsync(Administrator, Changes, new { kind = "AssignRole", subject, roleCode = "auditor", reason = "Cover" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> GetAsync(string subject, string path) => _host.SendAsync(subject, HttpMethod.Get, path, content: null);

    private Task<HttpResponseMessage> PostAsync(string subject, string path, object body) => _host.SendAsync(subject, HttpMethod.Post, path, Json(body));

    private Task<HttpResponseMessage> SendRawAsync(string subject, string path, string body, string contentType) =>
        _host.SendAsync(subject, HttpMethod.Post, path, new StringContent(body, Encoding.UTF8, contentType));

    private static JsonContent Json(object body) => JsonContent.Create(body);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

    private static string NewSubject(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}@example.test";

    private async Task<long> CountChangesAsync()
    {
        var sql = db.Provider == DatabaseProvider.SqlServer
            ? "SELECT COUNT(*) FROM [configuration].[AccessChangeRequest]"
            : "SELECT COUNT(*) FROM configuration.access_change_request";
        return Convert.ToInt64(await db.ScalarAsync(db.AdministrationAppConnectionString, sql, Ct), CultureInfo.InvariantCulture);
    }
}

/// <summary>The real API host on the parity database. Tokens are minted here only to exercise validation (test code, not an IdP).</summary>
internal sealed class ApiParityHost : IDisposable
{
    private const string Issuer = "https://idp.test/realms/test";
    private const string Audience = "iam-api";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;
    private readonly WebApplicationFactory<Program> _factory;

    public ApiParityHost(ParityDatabase db, int writesPerMinute = 10_000)
    {
        _key = new RsaSecurityKey(_rsa) { KeyId = "parity-key" };
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Authentication:Jwt:Authority", Issuer);
            builder.UseSetting("Authentication:Jwt:Audience", Audience);
            builder.UseSetting("IAM_DB_PROVIDER", db.Provider.ToString());
            builder.UseSetting("ConnectionStrings:identity", db.IdentityAppConnectionString);
            builder.UseSetting("ConnectionStrings:configuration", db.AdministrationAppConnectionString);
            builder.UseSetting("ConnectionStrings:catalog", db.CatalogAppConnectionString);
            builder.UseSetting("ConnectionStrings:organization", db.OrganizationAppConnectionString);
            builder.UseSetting(IamRateLimits.AdministrationWritesPerMinuteKey, writesPerMinute.ToString(CultureInfo.InvariantCulture));
            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(_key);
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                }));
        });
    }

    public async Task<HttpResponseMessage> SendAsync(string subject, HttpMethod method, string path, HttpContent? content)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(subject));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _rsa.Dispose();
    }

    private string Token(string subject)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity([new Claim("sub", subject)]),
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
    }
}
