using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Catalog.Infrastructure.Applications;
using IAM.Modules.Organization.Infrastructure.Workgroups;
using IAM.Modules.Workflow.Application.Approvers;
using IAM.Modules.Workflow.Domain.Approvals;
using IAM.Modules.Workflow.Infrastructure.Approvals;
using Shouldly;
using static IAM.Persistence.Tests.Parity.AccessControlParityTests;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// DL-073 end to end through the real host on both engines: participants read and act, outsiders get 404, permission
/// holders see the oversight, the inbox follows the levels, and bodies that overreach are refused.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class WorkflowApiParityTests(ParityDatabase db) : IDisposable
{
    private const string Instances = "/api/workflow/instances";
    private const string Tasks = "/api/workflow/my-tasks";
    private static readonly string[] Padded = [" padded"];
    private static readonly string[] OnePerson = ["p"];
    private readonly ApiParityHost _host = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task ParticipantsReadAndActAndOutsidersGetNotFound()
    {
        await new AccessControlParityTests(db).EnsureAccessBootstrappedAsync();
        var (a, b, outsider) = (Subject("a"), Subject("b"), Subject("outsider"));
        var (id, stages) = await StartAsync(Level(1, a), Level(2, b));

        using (var mine = await GetAsync(a, $"{Instances}/{id}"))
        {
            mine.StatusCode.ShouldBe(HttpStatusCode.OK, await mine.Content.ReadAsStringAsync(Ct));
            var view = await ReadAsync(mine);
            view.GetProperty("stages")[0].GetProperty("candidates")[0].GetString().ShouldBe(a);
            view.GetProperty("oversight").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        (await StatusAsync(outsider, $"{Instances}/{id}")).ShouldBe(HttpStatusCode.NotFound);
        (await StatusAsync(a, $"{Instances}/{Guid.CreateVersion7()}")).ShouldBe(HttpStatusCode.NotFound);
        using (var managed = await GetAsync(Manager, $"{Instances}/{id}"))
        {
            managed.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await ReadAsync(managed)).GetProperty("oversight").GetProperty("history").GetArrayLength().ShouldBeGreaterThan(0);
        }

        (await TaskInstancesAsync(a)).ShouldContain(id);
        (await TaskInstancesAsync(b)).ShouldNotContain(id);
        var version = await VersionAsync(a, id);

        using (var byOutsider = await PostAsync(outsider, $"{Instances}/{id}/decisions", Decision(version, stages[0], approve: true)))
        {
            byOutsider.StatusCode.ShouldBe(HttpStatusCode.NotFound, "an outsider can't tell the instance exists");
        }

        using (var stale = await PostAsync(a, $"{Instances}/{id}/decisions", Decision(version - 1, stages[0], approve: true)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await ReadAsync(stale)).GetProperty("code").GetString().ShouldBe("StaleVersion");
        }

        using (var approved = await PostAsync(a, $"{Instances}/{id}/decisions", Decision(version, stages[0], approve: true, reason: "Looks fine")))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
            (await ReadAsync(approved)).GetProperty("stages")[1].GetProperty("state").GetString().ShouldBe("Active");
        }

        (await TaskInstancesAsync(a)).ShouldNotContain(id);
        (await TaskInstancesAsync(b)).ShouldContain(id);
        version = await VersionAsync(b, id);

        using (var noReason = await PostAsync(b, $"{Instances}/{id}/decisions", Decision(version, stages[1], approve: false)))
        {
            noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var rejected = await PostAsync(b, $"{Instances}/{id}/decisions", Decision(version, stages[1], approve: false, reason: "Not justified")))
        {
            rejected.StatusCode.ShouldBe(HttpStatusCode.OK);
            var view = await ReadAsync(rejected);
            view.GetProperty("state").GetString().ShouldBe("Rejected");
            view.GetProperty("stages")[1].GetProperty("decisions")[0].GetProperty("reason").GetString().ShouldBe("Not justified");
            view.GetProperty("stages")[0].GetProperty("decisions")[0].GetProperty("reason").ValueKind.ShouldBe(JsonValueKind.Null, "an approval's reason is for the oversight only");
        }

        (await TaskInstancesAsync(b)).ShouldNotContain(id);
    }

    [Fact]
    public async Task EscalatingAndCancellingNeedTheirPermissions()
    {
        await new AccessControlParityTests(db).EnsureAccessBootstrappedAsync();
        var a = Subject("a");
        var (id, stages) = await StartAsync(Level(1, Requester));
        var version = await VersionAsync(Manager, id);
        var path = $"{Instances}/{id}/stages/{stages[0]}/escalation";

        using (var byRequester = await PostAsync(Requester.Value, path, new { expectedVersion = version, approvers = new[] { a }, reason = "Owner away" }))
        {
            byRequester.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the requester has no escalate permission");
        }

        using (var escalated = await PostAsync(Manager, path, new { expectedVersion = version, approvers = new[] { a }, reason = "Owner away" }))
        {
            escalated.StatusCode.ShouldBe(HttpStatusCode.OK, await escalated.Content.ReadAsStringAsync(Ct));
            version = (await ReadAsync(escalated)).GetProperty("version").GetInt64();
        }

        (await TaskInstancesAsync(a)).ShouldContain(id);
        using (var byApprover = await PostAsync(a, $"{Instances}/{id}/cancel", new { expectedVersion = version, reason = "Mine to cancel?" }))
        {
            byApprover.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var cancelled = await PostAsync(Manager, $"{Instances}/{id}/cancel", new { expectedVersion = version, reason = "No longer needed" }))
        {
            cancelled.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await ReadAsync(cancelled)).GetProperty("state").GetString().ShouldBe("Cancelled");
        }

        (await TaskInstancesAsync(a)).ShouldNotContain(id);
    }

    [Fact]
    public async Task AHeldRequestPutsNothingInTheInbox()
    {
        // One level holds (its only approver is the requester), so the parallel level can't take decisions either.
        await new AccessControlParityTests(db).EnsureAccessBootstrappedAsync();
        var a = Subject("a");
        var (id, _) = await StartAsync(Level(1, Requester), Level(1, a));
        using (var held = await GetAsync(a, $"{Instances}/{id}"))
        {
            (await ReadAsync(held)).GetProperty("state").GetString().ShouldBe("Held");
        }

        (await TaskInstancesAsync(a)).ShouldNotContain(id);
    }

    [Theory]
    [InlineData("""{"expectedVersion":1,"decisionId":"ID","stageId":"STAGE","approve":true,"onBehalfOf":"someone"}""")]
    [InlineData("""{"expectedVersion":1,"decisionId":"ID","stageId":"STAGE","approve":true,"actor":"someone"}""")]
    [InlineData("""{"decisionId":"ID","stageId":"STAGE","approve":true}""")]
    [InlineData("""{"expectedVersion":1,"decisionId":"00000000-0000-0000-0000-000000000000","stageId":"STAGE","approve":true}""")]
    [InlineData("""{"expectedVersion":1,"decisionId":"ID","stageId":"STAGE"}""")]
    [InlineData("""{"expectedVersion":"one","decisionId":"ID","stageId":"STAGE","approve":true}""")]
    public async Task OverreachingOrInvalidDecisionsAre400(string template)
    {
        await new AccessControlParityTests(db).EnsureAccessBootstrappedAsync();
        var a = Subject("a");
        var (id, stages) = await StartAsync(Level(1, a));
        var json = template.Replace("ID", Guid.CreateVersion7().ToString(), StringComparison.Ordinal).Replace("STAGE", stages[0].ToString(), StringComparison.Ordinal);
        using var response = await _host.SendAsync(a, HttpMethod.Post, $"{Instances}/{id}/decisions", new StringContent(json, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, json);
    }

    [Fact]
    public async Task BadApproverListsPagesAndOversizedBodiesAreRefused()
    {
        await new AccessControlParityTests(db).EnsureAccessBootstrappedAsync();
        var (id, stages) = await StartAsync(Level(1, Requester));
        var path = $"{Instances}/{id}/stages/{stages[0]}/escalation";
        using (var empty = await PostAsync(Manager, path, new { expectedVersion = 3, approvers = Array.Empty<string>(), reason = "r" }))
        {
            empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var bad = await PostAsync(Manager, path, new { expectedVersion = 3, approvers = Padded, reason = "r" }))
        {
            bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var tooMany = await PostAsync(Manager, path, new { expectedVersion = 3, approvers = Enumerable.Range(0, 257).Select(i => $"p{i}").ToArray(), reason = "r" }))
        {
            tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var huge = await PostAsync(Manager, path, new { expectedVersion = 3, approvers = OnePerson, reason = new string('x', 70_000) }))
        {
            huge.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        }

        (await StatusAsync(Manager, $"{Tasks}?pageSize=101")).ShouldBe(HttpStatusCode.BadRequest);
        (await StatusAsync(Manager, $"{Tasks}?page=0")).ShouldBe(HttpStatusCode.BadRequest);
        (await StatusAsync(Manager, $"{Tasks}?page=2&pageSize=5")).ShouldBe(HttpStatusCode.OK);
    }

    private static readonly PersonId Requester = PersonId.Create("wfapi-requester");

    private static string Subject(string prefix) => $"wfapi-{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private static StageDefinition Level(int sequence, string person) => Level(sequence, PersonId.Create(person));

    private static StageDefinition Level(int sequence, PersonId person) =>
        new(Guid.CreateVersion7(), sequence, QuorumMode.AnyOne, null, false, false, true, TimeSpan.FromHours(4), TimeoutAction.Escalate, ApproverSource.Named(person));

    private static object Decision(long version, Guid stage, bool approve, string? reason = null) =>
        new { expectedVersion = version, decisionId = Guid.CreateVersion7(), stageId = stage, approve, reason };

    /// <summary>Starts an instance the way the owning module will (DL-072b): through the service, never the API.</summary>
    private async Task<(Guid Id, Guid[] Stages)> StartAsync(params StageDefinition[] stages)
    {
        var id = Guid.CreateVersion7();
        var binding = new ApprovalBinding(id, Guid.CreateVersion7(), Guid.CreateVersion7(), new string('b', 64), Guid.CreateVersion7(), Guid.CreateVersion7(), Requester, PersonId.Create("wfapi-beneficiary"), PersonId.Create("wfapi-policy-maker"));
        var context = TestSupport.CreateWorkflow(db.Provider, db.WorkflowAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor("system:request")));
        var writer = new AuditWriter(TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor("system:request"), new AuditActionCatalog(WorkflowAuditActions.All.Select(x => new AuditActionRegistration(x))));
        var resolver = new ApproverResolver(
            new ApplicationApproverDirectory(TestSupport.CreateCatalog(db.Provider, db.CatalogAppConnectionString)),
            new WorkgroupDirectory(TestSupport.CreateOrganization(db.Provider, db.OrganizationAppConnectionString)),
            new ApproverResolutionSettings());
        var service = new ApprovalWorkflowService(context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(null), new NoPermissions(), resolver);
        var route = ApprovalRoute.Create(stages);
        await service.StartAsync(binding, route, Ct);
        return (id, [.. route.Stages.Select(s => s.StageId)]);
    }

    private async Task<long> VersionAsync(string subject, Guid id)
    {
        using var response = await GetAsync(subject, $"{Instances}/{id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await ReadAsync(response)).GetProperty("version").GetInt64();
    }

    private async Task<IReadOnlyList<Guid>> TaskInstancesAsync(string subject)
    {
        using var response = await GetAsync(subject, $"{Tasks}?pageSize=100");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return [.. (await ReadAsync(response)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("instanceId").GetGuid())];
    }

    private async Task<HttpStatusCode> StatusAsync(string subject, string path)
    {
        using var response = await GetAsync(subject, path);
        return response.StatusCode;
    }

    private Task<HttpResponseMessage> GetAsync(string subject, string path) => _host.SendAsync(subject, HttpMethod.Get, path, content: null);

    private Task<HttpResponseMessage> PostAsync(string subject, string path, object body) => _host.SendAsync(subject, HttpMethod.Post, path, JsonContent.Create(body));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

    private sealed class NoPermissions : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
