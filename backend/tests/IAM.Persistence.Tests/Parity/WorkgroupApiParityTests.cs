using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;
using static IAM.Persistence.Tests.Parity.AccessControlParityTests;

namespace IAM.Persistence.Tests.Parity;

/// <summary>DL-068 end to end through the real host on both engines: roles from access changes, a checker who isn't a member activates.</summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class WorkgroupApiParityTests(ParityDatabase db) : IDisposable
{
    private const string Workgroups = "/api/organization/workgroups";
    private const string Requests = "/api/organization/workgroup-change-requests";
    private static readonly string[] OneMember = ["a"];
    private readonly ApiParityHost _host = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task AnAdministratorProposesAndACheckerWhoIsNotAMemberActivates()
    {
        var (admin, checker) = await AdminAndCheckerAsync();
        var (_, memberChecker) = await AdminAndCheckerAsync();
        var key = "api_wg_" + Guid.NewGuid().ToString("N")[..8];
        Guid id;
        using (var registered = await PostAsync(admin, Workgroups, new { key, displayName = "Finance approvers", members = new[] { memberChecker, "someone" } }))
        {
            registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync(Ct));
            id = (await ReadAsync(registered)).GetProperty("id").GetGuid();
        }

        Guid requestId;
        string hash;
        using (var proposed = await PostAsync(admin, $"{Workgroups}/{id}/change-requests", new { reason = "Set up" }))
        {
            proposed.StatusCode.ShouldBe(HttpStatusCode.Created);
            var body = await ReadAsync(proposed);
            requestId = body.GetProperty("id").GetGuid();
            hash = body.GetProperty("contentHash").GetString()!;
        }

        using (var byMember = await PostAsync(memberChecker, $"{Requests}/{requestId}/approve", new { contentHash = hash, reason = (string?)null }))
        {
            byMember.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ReadAsync(byMember)).GetProperty("code").GetString().ShouldBe("MemberCannotDecide");
        }

        using (var approved = await PostAsync(checker, $"{Requests}/{requestId}/approve", new { contentHash = hash, reason = "Checked" }))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        }

        using var found = await GetAsync(checker, $"{Workgroups}/{key}");
        found.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await ReadAsync(found);
        view.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        view.GetProperty("active").GetProperty("members").EnumerateArray().Select(m => m.GetString()).ShouldBe(new[] { memberChecker, "someone" }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task OnlyPeopleWithThePermissionsCanUseWorkgroups()
    {
        var (_, checker) = await AdminAndCheckerAsync();
        using (var read = await GetAsync("outsider-" + Guid.NewGuid().ToString("N")[..8], $"{Workgroups}/iam_managers"))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var register = await PostAsync(checker, Workgroups, new { key = "checker_tries", displayName = "No", members = Array.Empty<string>() });
        register.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("""{"key":"KEY","displayName":"X","members":[],"isActive":true}""")]
    [InlineData("""{"key":"KEY","displayName":"X","members":["a","a"]}""")]
    [InlineData("""{"key":"KEY","displayName":"X","members":[""]}""")]
    [InlineData("""{"key":"KEY","displayName":"X"}""")]
    [InlineData("""{"key":"Bad-Key","displayName":"X","members":[]}""")]
    [InlineData("""{"key":"KEY","displayName":"X","members":[],"key":"other"}""")]
    public async Task OverreachingOrInvalidBodiesAre400(string template)
    {
        var (admin, _) = await AdminAndCheckerAsync();
        var json = template.Replace("KEY", "wg_" + Guid.NewGuid().ToString("N")[..8], StringComparison.Ordinal);
        using var response = await _host.SendAsync(admin, HttpMethod.Post, Workgroups, new StringContent(json, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, json);

        using var clean = await PostAsync(admin, Workgroups, new { key = "wg_" + Guid.NewGuid().ToString("N")[..8], displayName = "X", members = OneMember });
        clean.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private async Task<(string Admin, string Checker)> AdminAndCheckerAsync()
    {
        await new AccessControlParityTests(db).EnsureAccessBootstrappedAsync();
        var admin = "wgadmin-" + Guid.NewGuid().ToString("N")[..8];
        var checker = "wgchecker-" + Guid.NewGuid().ToString("N")[..8];
        foreach (var (person, role) in new[] { (admin, "application-administrator"), (checker, "application-checker") })
        {
            Guid id;
            using (var proposed = await PostAsync(Administrator, "/api/administration/changes", new { kind = "AssignRole", subject = person, roleCode = role, reason = "Workgroup test" }))
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
