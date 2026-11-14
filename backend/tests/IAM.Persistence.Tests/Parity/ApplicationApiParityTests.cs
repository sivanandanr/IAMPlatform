using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;
using static IAM.Persistence.Tests.Parity.AccessControlParityTests;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// DL-066 end to end on SQL Server and PostgreSQL, through the real host: people get the Application administrator and
/// checker roles through the access-change process; an administrator registers an application and proposes activating it;
/// only a checker who is not the proposed owner can activate it; bodies that overreach are refused.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class ApplicationApiParityTests(ParityDatabase db) : IDisposable
{
    private const string Applications = "/api/catalog/applications";
    private const string Requests = "/api/catalog/application-change-requests";
    private const string RoleTypes = "/api/catalog/application-role-types";
    private readonly ApiParityHost _host = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task AnAdministratorProposesAndOnlyACheckerWhoIsNotTheOwnerActivates()
    {
        var (admin, checker) = await AdminAndCheckerAsync();
        var (_, ownerChecker) = await AdminAndCheckerAsync();
        var key = NewKey("api_app");

        Guid applicationId;
        using (var registered = await PostAsync(admin, Applications, new { key, displayName = "Finance ERP", settings = Settings(ownerChecker) }))
        {
            registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync(Ct));
            var body = await ReadAsync(registered);
            body.GetProperty("isActive").GetBoolean().ShouldBeFalse();
            body.GetProperty("draft").GetProperty("roles")[0].GetProperty("roleTypeCode").GetString().ShouldBe("it_admin");
            applicationId = body.GetProperty("id").GetGuid();
        }

        Guid requestId;
        using (var proposed = await PostAsync(admin, $"{Applications}/{applicationId}/change-requests", new { reason = "Go live" }))
        {
            proposed.StatusCode.ShouldBe(HttpStatusCode.Created);
            var body = await ReadAsync(proposed);
            body.GetProperty("maker").GetString().ShouldBe(admin);
            body.GetProperty("proposedOwner").GetString().ShouldBe(ownerChecker);
            requestId = body.GetProperty("id").GetGuid();
        }

        string contentHash;
        using (var review = await GetAsync(checker, $"{Requests}/{requestId}"))
        {
            review.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await ReadAsync(review);
            contentHash = body.GetProperty("draft").GetProperty("contentHash").GetString()!;
            body.GetProperty("request").GetProperty("contentHash").GetString().ShouldBe(contentHash);
        }

        using (var noHash = await PostAsync(checker, $"{Requests}/{requestId}/approve", new { reason = "Checked" }))
        {
            noHash.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "approving needs the reviewed content hash");
        }

        using (var byAdmin = await PostAsync(admin, $"{Requests}/{requestId}/approve", new { contentHash, reason = (string?)null }))
        {
            byAdmin.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the administrator holds no approve permission");
        }

        using (var byOwner = await PostAsync(ownerChecker, $"{Requests}/{requestId}/approve", new { contentHash, reason = (string?)null }))
        {
            byOwner.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ReadAsync(byOwner)).GetProperty("code").GetString().ShouldBe("OwnerCannotDecide");
        }

        using (var approved = await PostAsync(checker, $"{Requests}/{requestId}/approve", new { contentHash, reason = "Checked" }))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
            (await ReadAsync(approved)).GetProperty("state").GetString().ShouldBe("Approved");
        }

        using (var found = await GetAsync(checker, $"{Applications}/{key}"))
        {
            found.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await ReadAsync(found);
            body.GetProperty("isActive").GetBoolean().ShouldBeTrue();
            body.GetProperty("active").GetProperty("owner").GetString().ShouldBe(ownerChecker);
        }

        using var replay = await PostAsync(checker, $"{Requests}/{requestId}/approve", new { contentHash, reason = (string?)null });
        replay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadAsync(replay)).GetProperty("code").GetString().ShouldBe("NotProposed");
    }

    [Fact]
    public async Task OnlyPeopleWithThePermissionsCanUseTheCatalog()
    {
        var (admin, checker) = await AdminAndCheckerAsync();
        var outsider = "outsider-" + Guid.NewGuid().ToString("N")[..8];
        foreach (var path in new[] { $"{Applications}/some_key", RoleTypes, $"{Requests}/{Guid.NewGuid()}" })
        {
            using var read = await GetAsync(outsider, path);
            read.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }

        using (var register = await PostAsync(checker, Applications, new { key = NewKey("checker_tries"), displayName = "Nope", settings = Settings("owner-1") }))
        {
            register.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a checker can't manage the catalog");
        }

        using (var missing = await GetAsync(admin, $"{Applications}/{NewKey("missing")}"))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // Nobody can hold both duties (DL-066d).
        using var both = await PostAsync(Administrator, "/api/administration/changes", new { kind = "AssignRole", subject = admin, roleCode = "application-checker", reason = "Both" });
        both.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadAsync(both)).GetProperty("code").GetString().ShouldBeOneOf("IncompatibleRoles", "SeparatedDuties");
    }

    [Fact]
    public async Task AnEmptyRequiredRoleIsRefusedWithACode()
    {
        var (admin, _) = await AdminAndCheckerAsync();
        Guid applicationId;
        using (var registered = await PostAsync(admin, Applications, new { key = NewKey("empty_role"), displayName = "X", settings = Settings("owner-1", holderKind: null, holderId: null) }))
        {
            registered.StatusCode.ShouldBe(HttpStatusCode.Created);
            applicationId = (await ReadAsync(registered)).GetProperty("id").GetGuid();
        }

        using var proposed = await PostAsync(admin, $"{Applications}/{applicationId}/change-requests", new { reason = "Go live" });
        proposed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadAsync(proposed)).GetProperty("code").GetString().ShouldBe("RequiredRoleEmpty");

        using var noReason = await PostAsync(admin, $"{Applications}/{applicationId}/change-requests", new { reason = " " });
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private const string CleanSettings = """{"owner":"owner-1","roles":[{"roleTypeCode":"it_admin","requirement":"Required","holderKind":"Group","holderId":"grp-1"}]}""";

    [Theory]
    [InlineData("""{"key":"KEY","displayName":"X","settings":SETTINGS,"isActive":true}""")]
    [InlineData("""{"key":"KEY","displayName":"X","settings":{"owner":"owner-1","roles":[],"contentHash":"x"}}""")]
    [InlineData("""{"key":"KEY","displayName":"X","settings":{"owner":"owner-1","roles":[{"roleTypeCode":"it_admin","requirement":"Required","holderKind":"Group","holderId":"g","roleTypeId":"x"}]}}""")]
    [InlineData("""{"key":"KEY","displayName":"X","settings":{"owner":"owner-1","roles":[{"roleTypeCode":"it_admin","requirement":"required","holderKind":"Group","holderId":"g"}]}}""")]
    [InlineData("""{"key":"KEY","displayName":"X","settings":{"owner":"owner-1","roles":[{"roleTypeCode":"it_admin","requirement":"2","holderKind":"Group","holderId":"g"}]}}""")]
    [InlineData("""{"key":"KEY","displayName":"X","settings":{"owner":"owner-1","roles":[{"roleTypeCode":"it_admin","requirement":"Required","holderKind":"Group"}]}}""")]
    [InlineData("""{"key":"KEY","displayName":"X","settings":{"owner":"owner-1"}}""")]
    [InlineData("""{"key":"KEY","displayName":"X","settings":SETTINGS,"key":"other"}""")]
    [InlineData("""{"key":"KEY","displayName":"X","settings":{"owner":"owner-1","roles":[{"roleTypeCode":"it_admin","requirement":"Required","holderKind":"Group","holderId":"g"},{"roleTypeCode":"it_admin","requirement":"Optional"}]}}""")]
    [InlineData("""{"key":"Bad Key!","displayName":"X","settings":SETTINGS}""")]
    public async Task OverreachingOrInvalidBodiesAre400(string template)
    {
        var (admin, _) = await AdminAndCheckerAsync();
        var json = template.Replace("KEY", NewKey("mass"), StringComparison.Ordinal).Replace("SETTINGS", CleanSettings, StringComparison.Ordinal);
        using var response = await _host.SendAsync(admin, HttpMethod.Post, Applications, new StringContent(json, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, json);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("grp-1", Case.Sensitive, "errors never echo values");

        // The same body without the overreach is accepted, so the 400 above is for the overreach itself.
        var clean = $$"""{"key":"{{NewKey("clean")}}","displayName":"X","settings":{{CleanSettings}}}""";
        using var ok = await _host.SendAsync(admin, HttpMethod.Post, Applications, new StringContent(clean, Encoding.UTF8, "application/json"));
        ok.StatusCode.ShouldBe(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task TooManyRolesAre400()
    {
        var (admin, _) = await AdminAndCheckerAsync();
        var roles = Enumerable.Range(0, 33).Select(i => new { roleTypeCode = $"type_{i}", requirement = "Optional" }).ToArray();
        using var response = await PostAsync(admin, Applications, new { key = NewKey("many"), displayName = "X", settings = new { owner = "owner-1", roles } });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OversizedBodiesAre413()
    {
        var (admin, _) = await AdminAndCheckerAsync();
        using var response = await PostAsync(admin, Applications, new { key = NewKey("big"), displayName = new string('x', 40 * 1024), settings = Settings("owner-1") });
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task ARoleTypeIsProposedAndApprovedByDifferentPeople()
    {
        var (admin, checker) = await AdminAndCheckerAsync();
        var code = NewKey("security_contact");
        Guid id;
        using (var proposed = await PostAsync(admin, RoleTypes, new { code, displayName = "Security contact", reason = "Audit asked" }))
        {
            proposed.StatusCode.ShouldBe(HttpStatusCode.Created, await proposed.Content.ReadAsStringAsync(Ct));
            id = (await ReadAsync(proposed)).GetProperty("id").GetGuid();
        }

        using (var noReason = await PostAsync(checker, $"{RoleTypes}/{id}/reject", new { reason = (string?)null }))
        {
            noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var self = await PostAsync(admin, $"{RoleTypes}/{id}/approve", new { reason = (string?)null }))
        {
            self.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var approved = await PostAsync(checker, $"{RoleTypes}/{id}/approve", new { reason = "Agreed" }))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        }

        using var list = await GetAsync(checker, RoleTypes);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        // A code can appear more than once (a withdrawn or rejected type frees its code), so look rows up by id or state.
        var types = (await ReadAsync(list)).EnumerateArray().ToList();
        types.Single(t => t.GetProperty("id").GetGuid() == id).GetProperty("state").GetString().ShouldBe("Approved");
        foreach (var builtIn in new[] { "user_admin", "it_admin", "technical_custodian" })
        {
            types.Single(t => t.GetProperty("code").GetString() == builtIn).GetProperty("state").GetString().ShouldBe("BuiltIn");
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static string NewKey(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    private static object Settings(string owner, string? holderKind = "Group", string? holderId = "grp-erp-it") => new
    {
        owner,
        roles = new[] { new { roleTypeCode = "it_admin", requirement = "Required", holderKind, holderId } },
    };

    /// <summary>Two new people given the Application administrator and checker roles through the access-change process.</summary>
    private async Task<(string Admin, string Checker)> AdminAndCheckerAsync()
    {
        await new AccessControlParityTests(db).EnsureAccessBootstrappedAsync();
        var admin = "appadmin-" + Guid.NewGuid().ToString("N")[..8];
        var checker = "appchecker-" + Guid.NewGuid().ToString("N")[..8];
        foreach (var (person, role) in new[] { (admin, "application-administrator"), (checker, "application-checker") })
        {
            Guid id;
            using (var proposed = await PostAsync(Administrator, "/api/administration/changes", new { kind = "AssignRole", subject = person, roleCode = role, reason = "Catalog test" }))
            {
                proposed.StatusCode.ShouldBe(HttpStatusCode.Created, await proposed.Content.ReadAsStringAsync(Ct));
                id = (await ReadAsync(proposed)).GetProperty("id").GetGuid();
            }

            using var approved = await PostAsync(Manager, $"/api/administration/changes/{id}/approve", new { reason = (string?)null });
            approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        return (admin, checker);
    }

    private Task<HttpResponseMessage> GetAsync(string subject, string path) => _host.SendAsync(subject, HttpMethod.Get, path, content: null);

    private Task<HttpResponseMessage> PostAsync(string subject, string path, object body) => _host.SendAsync(subject, HttpMethod.Post, path, JsonContent.Create(body));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
}
