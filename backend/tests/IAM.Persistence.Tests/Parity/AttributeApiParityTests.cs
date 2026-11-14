using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;
using static IAM.Persistence.Tests.Parity.AccessControlParityTests;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// DL-007 / DL-064 end to end on SQL Server and PostgreSQL, through the real host: people get the policy author and checker
/// roles through the access-change process, the author registers an attribute and proposes publishing it, and only a
/// checker (never the author) can publish it.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class AttributeApiParityTests(ParityDatabase db) : IDisposable
{
    private const string Attributes = "/api/identity/attributes";
    private const string Requests = "/api/identity/attribute-publish-requests";
    private readonly ApiParityHost _host = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task AnAuthorProposesAndOnlyACheckerPublishes()
    {
        var (author, checker) = await AuthorAndCheckerAsync();
        var key = "api_attr_" + Guid.NewGuid().ToString("N")[..8];

        using (var registered = await PostAsync(author, Attributes, new { key, draft = Draft("Grade band") }))
        {
            registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync(Ct));
        }

        Guid definitionId;
        using (var draft = await GetAsync(author, $"{Attributes}/{key}"))
        {
            draft.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await ReadAsync(draft);
            body.GetProperty("state").GetString().ShouldBe("Draft");
            body.GetProperty("dataType").GetString().ShouldBe("Code");
            definitionId = body.GetProperty("definitionId").GetGuid();
        }

        Guid requestId;
        using (var proposed = await PostAsync(author, $"{Attributes}/{definitionId}/publish-requests", new { reason = "Needed for the matrix" }))
        {
            proposed.StatusCode.ShouldBe(HttpStatusCode.Created);
            var body = await ReadAsync(proposed);
            body.GetProperty("maker").GetString().ShouldBe(author);
            requestId = body.GetProperty("id").GetGuid();
        }

        // The checker reviews the request with the draft it would publish, and names that content when approving.
        string contentHash;
        using (var review = await GetAsync(checker, $"{Requests}/{requestId}"))
        {
            review.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await ReadAsync(review);
            var draft = body.GetProperty("draft");
            draft.GetProperty("displayName").GetString().ShouldBe("Grade band");
            contentHash = draft.GetProperty("contentHash").GetString()!;
            body.GetProperty("request").GetProperty("contentHash").GetString().ShouldBe(contentHash);
        }

        using (var noHash = await PostAsync(checker, $"{Requests}/{requestId}/approve", new { reason = "Checked" }))
        {
            noHash.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "approving needs the reviewed content hash");
        }

        // The author holds no approve permission, so the check refuses before the request is even read.
        using (var selfApproval = await PostAsync(author, $"{Requests}/{requestId}/approve", new { contentHash, reason = (string?)null }))
        {
            selfApproval.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var approved = await PostAsync(checker, $"{Requests}/{requestId}/approve", new { contentHash, reason = "Checked" }))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
            (await ReadAsync(approved)).GetProperty("state").GetString().ShouldBe("Approved");
        }

        // Published: no draft left; a replay is a conflict.
        using (var noDraft = await GetAsync(author, $"{Attributes}/{key}"))
        {
            noDraft.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using var replay = await PostAsync(checker, $"{Requests}/{requestId}/approve", new { contentHash, reason = (string?)null });
        replay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadAsync(replay)).GetProperty("code").GetString().ShouldBe("NotProposed");
    }

    [Fact]
    public async Task OnlyPeopleWithThePermissionsCanUseTheCatalog()
    {
        var (_, checker) = await AuthorAndCheckerAsync();
        var outsider = "outsider-" + Guid.NewGuid().ToString("N")[..8];
        using (var read = await GetAsync(outsider, $"{Attributes}/some_key"))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var register = await PostAsync(checker, Attributes, new { key = "checker_tries", draft = Draft("Nope") }))
        {
            register.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a checker can't manage the catalog");
        }
    }

    [Theory]
    [InlineData("""{"key":"mass_assign","draft":{"displayName":"X","dataType":"Code","subjectScope":"Beneficiary","sourceSystem":"hr","sensitivity":"Internal","freshnessMaxAgeMinutes":60,"maxLength":10,"operators":["Equals"]},"state":"Published"}""")]
    [InlineData("""{"key":"mass_assign","draft":{"displayName":"X","dataType":"Code","subjectScope":"Beneficiary","sourceSystem":"hr","sensitivity":"Internal","freshnessMaxAgeMinutes":60,"maxLength":10,"operators":["Equals"],"contentHash":"x"}}""")]
    [InlineData("""{"key":"mass_assign"}""")]
    [InlineData("""{"key":"mass_assign","draft":{"displayName":"X","dataType":"Code","subjectScope":"Beneficiary","sourceSystem":"hr","sensitivity":"Internal","freshnessMaxAgeMinutes":60,"maxLength":10,"operators":["Equals"]},"key":"other"}""")]
    public async Task OverreachingBodiesAre400(string json)
    {
        var (author, _) = await AuthorAndCheckerAsync();
        using var response = await _host.SendAsync(author, HttpMethod.Post, Attributes, new StringContent(json, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The same body without the overreach is accepted, so the 400 above is for the overreach itself.
        var key = "clean_" + Guid.NewGuid().ToString("N")[..8];
        using var clean = await PostAsync(author, Attributes, new { key, draft = Draft("Clean") });
        clean.StatusCode.ShouldBe(HttpStatusCode.Created, await clean.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task NobodyCanBeBothAuthorAndChecker()
    {
        var (author, _) = await AuthorAndCheckerAsync();
        using var proposed = await PostAsync(Administrator, "/api/administration/changes", new { kind = "AssignRole", subject = author, roleCode = "policy-checker", reason = "Both" });
        proposed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadAsync(proposed)).GetProperty("code").GetString().ShouldBeOneOf("IncompatibleRoles", "SeparatedDuties");
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static object Draft(string displayName) => new
    {
        displayName,
        dataType = "Code",
        subjectScope = "Beneficiary",
        sourceSystem = "successfactors",
        sensitivity = "Internal",
        normalization = "None",
        freshnessMaxAgeMinutes = 1440,
        maxLength = 10,
        operators = new[] { "Equals", "In" },
    };

    /// <summary>Two new people given the policy author and checker roles through the access-change process.</summary>
    private async Task<(string Author, string Checker)> AuthorAndCheckerAsync()
    {
        await new AccessControlParityTests(db).EnsureAccessBootstrappedAsync();
        var author = "author-" + Guid.NewGuid().ToString("N")[..8];
        var checker = "checker-" + Guid.NewGuid().ToString("N")[..8];
        foreach (var (person, role) in new[] { (author, "policy-author"), (checker, "policy-checker") })
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

        return (author, checker);
    }

    private Task<HttpResponseMessage> GetAsync(string subject, string path) => _host.SendAsync(subject, HttpMethod.Get, path, content: null);

    private Task<HttpResponseMessage> PostAsync(string subject, string path, object body) => _host.SendAsync(subject, HttpMethod.Post, path, JsonContent.Create(body));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
}
