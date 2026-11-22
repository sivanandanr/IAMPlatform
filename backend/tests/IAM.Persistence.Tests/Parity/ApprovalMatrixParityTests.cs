using System.Globalization;
using IAM.BuildingBlocks.Persistence;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Audit.Contracts;
using IAM.Modules.Audit.Infrastructure;
using IAM.Modules.Identity.Contracts;
using IAM.Modules.Policy.Application.Matrix;
using IAM.Modules.Policy.Contracts;
using IAM.Modules.Policy.Domain.Matrix;
using IAM.Modules.Policy.Infrastructure.Matrix;
using IAM.Modules.Policy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace IAM.Persistence.Tests.Parity;

/// <summary>
/// Stage 8 slice 3 approval matrix behaviour that must be identical on SQL Server and PostgreSQL (DL-090): drafts are compiled
/// on save, overlaps block publication, a second person who neither proposed nor wrote it activates a version, the selector
/// fails closed, and the database refuses the same things by SQL. One enterprise matrix exists per database, so these tests
/// run in order inside one test.
/// </summary>
[Collection(ParityDatabaseGroup.Name)]
[Trait("Category", "DatabaseParity")]
public sealed class ApprovalMatrixParityTests(ParityDatabase db)
{
    private const string Author = "matrix-author";
    private const string Checker = "matrix-checker";
    private static readonly Guid PrivilegedRule = Guid.Parse("0193d000-0000-7000-8000-000000000001");
    private static readonly Guid DefaultRule = Guid.Parse("0193d000-0000-7000-8000-000000000002");
    private static readonly Guid Department = Guid.Parse("0193d000-0000-7000-8000-0000000000aa");

    private static readonly FakePermissions DefaultPermissions = new(
        (Author, MatrixAuditActions.ManageAction), (Author, MatrixAuditActions.ReadAction),
        (Checker, MatrixAuditActions.ApproveAction), (Checker, MatrixAuditActions.ReadAction));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool IsSqlServer => db.Provider == DatabaseProvider.SqlServer;

    [Fact]
    public async Task TheMatrixIsDraftedCheckedActivatedByASecondPersonAndSelectsRoutes()
    {
        db.PolicyFirstRunApplied.Count.ShouldBe(2);
        db.PolicyFirstRunApplied[0].ShouldEndWith("_ApprovalMatrix");
        db.PolicyFirstRunApplied[1].ShouldEndWith("_MatrixGuards");
        db.PolicySecondRunPending.ShouldBeEmpty();
        var catalog = new FakeAttributes();

        // Nothing active yet: everything holds.
        await using (var context = Context(Author))
        {
            (await Selector(context, catalog).SelectAsync([], DateTimeOffset.UtcNow, Ct)).Outcome.ShouldBe(MatrixSelection.HoldNoMatrix);
        }

        // Refused drafts: an unpublished attribute; a route with no decision. Overlaps save, but can't be proposed.
        await using (var scope = Service(Author, catalog))
        {
            (await Should.ThrowAsync<MatrixRefusedException>(() => scope.Service.SetDraftAsync(null, Matrix(Rule(DefaultRule, 1, DepartmentIs("FIN"), Stage())), Ct)))
                .Refusal.ShouldBe(MatrixRefusal.UnknownAttribute);
            await Should.ThrowAsync<MatrixRejectedException>(() => scope.Service.SetDraftAsync(null, Matrix(Rule(DefaultRule, 1, AnyApplication(), Stage("Action", fields: "[\"AccountName\"]"), true)), Ct));
            var overlapping = await scope.Service.SetDraftAsync(null, Matrix(Rule(PrivilegedRule, 100, Privileged(true), Stage()), Rule(DefaultRule, 100, AnyApplication(), Stage(), true)), Ct);
            overlapping.Draft.ShouldNotBeNull().Overlaps.ShouldNotBeEmpty();
            (await Should.ThrowAsync<MatrixRefusedException>(() => scope.Service.ProposeAsync("Ready", Ct))).Refusal.ShouldBe(MatrixRefusal.OverlapsFound);

            var fixedDraft = await scope.Service.SetDraftAsync(overlapping.ConcurrencyVersion, Standard(), Ct);
            fixedDraft.Draft.ShouldNotBeNull().Overlaps.ShouldBeEmpty();
            fixedDraft.Draft.EditedBy.ShouldBe(Author);
        }

        MatrixChangeRequestView proposed;
        await using (var scope = Service(Author, catalog))
        {
            proposed = await scope.Service.ProposeAsync("Standard routes", Ct);
        }

        // The author can't approve even with the permission; a stale hash is refused; the checker activates it.
        var both = new FakePermissions((Author, MatrixAuditActions.ApproveAction), (Author, MatrixAuditActions.ManageAction), (Checker, MatrixAuditActions.ApproveAction));
        await using (var scope = Service(Author, catalog, both))
        {
            (await Should.ThrowAsync<MatrixRefusedException>(() => scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, null, Ct))).Refusal.ShouldBe(MatrixRefusal.MakerCannotDecide);
        }

        await using (var scope = Service(Checker, catalog, both))
        {
            (await Should.ThrowAsync<MatrixRefusedException>(() => scope.Service.ApproveAsync(proposed.Id, new string('0', 64), null, Ct))).Refusal.ShouldBe(MatrixRefusal.DraftChanged);
            (await scope.Service.ApproveAsync(proposed.Id, proposed.ContentHash, "Checked", Ct)).State.ShouldBe("Approved");
        }

        // The selector picks the privileged route, holds on a missing fact, and names the version as evidence.
        var now = DateTimeOffset.UtcNow;
        await using (var context = Context(Author))
        {
            var selector = Selector(context, catalog);
            var privileged = await selector.SelectAsync(Facts(now, privileged: true), now, Ct);
            privileged.IsMatched.ShouldBeTrue();
            privileged.MatchedRuleId.ShouldBe(PrivilegedRule);
            privileged.Route.Select(s => (s.Sequence, s.Source, s.WorkgroupKey, s.Mandatory, s.Independent)).ShouldBe([(1, "EntitlementOwner", null, false, false), (2, "Workgroup", "iam_security", true, true)]);
            privileged.ContentHash.ShouldBe(proposed.ContentHash);
            privileged.EvidenceJson.ShouldNotBeNull().ShouldContain(privileged.VersionId!.Value.ToString("D"));

            (await selector.SelectAsync(Facts(now, privileged: false), now, Ct)).MatchedRuleId.ShouldBe(DefaultRule);
            var unknown = await selector.SelectAsync(Facts(now, privileged: null), now, Ct);
            unknown.IsMatched.ShouldBeFalse();
            unknown.Route.ShouldBeEmpty();
        }

        await DatabaseRefusesForgeryAndChangesAsync(catalog, proposed);
        (await AuditCountAsync(MatrixAuditActions.Approved, "Succeeded")).ShouldBe(1);
        (await AuditCountAsync(MatrixAuditActions.Refused, "Denied")).ShouldBeGreaterThanOrEqualTo(4);
    }

    private async Task DatabaseRefusesForgeryAndChangesAsync(FakeAttributes catalog, MatrixChangeRequestView approved)
    {
        // A second draft, proposed by the author and then forged as approved by its own author: activation is refused.
        ApprovalMatrixView view;
        await using (var scope = Service(Author, catalog))
        {
            var current = (await scope.Service.FindAsync(Ct)).ShouldNotBeNull();
            view = await scope.Service.SetDraftAsync(current.ConcurrencyVersion, Standard(hours: 48), Ct);
        }

        var draft = view.Draft!;
        var forged = Guid.CreateVersion7();
        string[] columns = ["Id", "MatrixId", "VersionId", "VersionNumber", "ContentHash", "CompiledHash", "Maker", "Reason", "ProposedAtUtc", "State", "Checker", "DecidedAtUtc", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "Version"];
        await db.ExecuteAsync(
            db.PolicyAppConnectionString,
            $"INSERT INTO {Table("MatrixChangeRequest")} ({string.Join(", ", columns.Select(Col))}) VALUES ('{forged}', '{view.Id}', '{draft.VersionId}', {draft.VersionNumber}, '{draft.ContentHash}', '{draft.CompiledHash}', 'maker', 'r', '2026-11-17T00:00:00+00:00', 'Approved', '{Author}', '2026-11-17T00:00:00+00:00', '2026-11-17T00:00:00+00:00', 't', '2026-11-17T00:00:00+00:00', 't', 1)",
            Ct);
        foreach (var login in Logins)
        {
            (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(
                login,
                $"UPDATE {Table("MatrixVersion")} SET {Col("State")} = 'Active', {Col("ActivatedByRequestId")} = '{forged}' WHERE {Col("Id")} = '{draft.VersionId}'",
                Ct))).Message.ShouldMatch("(?i)(DL-090|51305|unique)");
        }

        var active = (await ActiveVersionIdAsync()).ShouldNotBeNull();
        foreach (var login in Logins)
        {
            foreach (var sql in new[]
            {
                $"UPDATE {Table("MatrixVersion")} SET {Col("Document")} = '{{}}' WHERE {Col("Id")} = '{active}'",
                $"UPDATE {Table("MatrixVersion")} SET {Col("CompiledHash")} = '{new string('1', 64)}' WHERE {Col("Id")} = '{active}'",
                $"DELETE FROM {Table("MatrixVersion")} WHERE {Col("Id")} = '{active}'",
                $"UPDATE {Table("MatrixChangeRequest")} SET {Col("Checker")} = 'x' WHERE {Col("Id")} = '{approved.Id}'",
                $"DELETE FROM {Table("MatrixChangeRequest")} WHERE {Col("Id")} = '{approved.Id}'",
                $"UPDATE {Table("ApprovalMatrix")} SET {Col("Key")} = 'other' WHERE {Col("Id")} = '{view.Id}'",
                $"DELETE FROM {Table("ApprovalMatrix")} WHERE {Col("Id")} = '{view.Id}'",
            })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(login, sql, Ct), sql)).Message.ShouldNotBeNullOrEmpty();
            }
        }

        if (!IsSqlServer)
        {
            foreach (var table in new[] { "approval_matrix", "matrix_version", "matrix_change_request" })
            {
                (await Should.ThrowAsync<Exception>(() => db.ExecuteAsync(db.MigratorConnectionString, $"TRUNCATE policy.{table} CASCADE", Ct), table)).Message.ShouldMatch("(?i)DL-090");
            }
        }
    }

    private static string Stage(string kind = "Decision", string source = "EntitlementOwner", string? workgroup = null, int sequence = 1, bool mandatory = false, bool independent = false, int hours = 72, string fields = "[]") =>
        $$"""{"sequence":{{sequence}},"kind":"{{kind}}","source":"{{source}}","roleTypeCode":null,"workgroupKey":{{(workgroup is null ? "null" : $"\"{workgroup}\"")}},"quorum":"AnyOne","minimumApprovals":null,"mandatory":{{(mandatory ? "true" : "false")}},"independent":{{(independent ? "true" : "false")}},"deadlineHours":{{hours}},"onTimeout":"Escalate","fields":{{fields}}}""";

    private static string Rule(Guid id, int priority, string condition, string route, bool wildcard = false) =>
        $$"""{"ruleId":"{{id:D}}","priority":{{priority}},"effectiveFromUtc":null,"effectiveToUtc":null,"allowsWildcard":{{(wildcard ? "true" : "false")}},"condition":{{condition}},"route":[{{route}}]}""";

    private static string Matrix(params string[] rules) => $$"""{"rules":[{{string.Join(',', rules)}}]}""";

    private static string Privileged(bool value) =>
        $$$"""{"schemaVersion":1,"root":{"type":"condition","subject":"Entitlement","attributeId":"{{{BuiltInFactIds.EntitlementPrivileged:D}}}","operator":"Equals","values":["{{{(value ? "true" : "false")}}}"]}}""";

    private static string AnyApplication() =>
        $$$"""{"schemaVersion":1,"root":{"type":"wildcard","subject":"Application","attributeId":"{{{BuiltInFactIds.ApplicationKey:D}}}"}}""";

    private static string DepartmentIs(string value) =>
        $$$"""{"schemaVersion":1,"root":{"type":"condition","subject":"Beneficiary","attributeId":"{{{Department:D}}}","operator":"Equals","values":["{{{value}}}"]}}""";

    private static string Standard(int hours = 72) => Matrix(
        Rule(PrivilegedRule, 200, Privileged(true), Stage(hours: hours) + "," + Stage(source: "Workgroup", workgroup: "iam_security", sequence: 2, mandatory: true, independent: true, hours: hours)),
        Rule(DefaultRule, 100, AnyApplication(), Stage(hours: hours), true));

    private static List<MatrixFact> Facts(DateTimeOffset now, bool? privileged)
    {
        List<MatrixFact> facts = [new("Application", BuiltInFactIds.ApplicationKey, "erp", now)];
        if (privileged is { } p)
        {
            facts.Add(new("Entitlement", BuiltInFactIds.EntitlementPrivileged, p ? "true" : "false", now));
        }

        return facts;
    }

    private string[] Logins => [db.PolicyAppConnectionString, db.MigratorConnectionString];

    private string Table(string name) => IsSqlServer ? $"[policy].[{name}]" : $"policy.{IamModelConventions.ToSnakeCase(name)}";

    private string Col(string name) => IsSqlServer ? $"[{name}]" : IamModelConventions.ToSnakeCase(name);

    private async Task<Guid?> ActiveVersionIdAsync()
    {
        await using var context = Context(Author);
        return await context.MatrixVersions.Where(v => v.State == MatrixVersionState.Active).Select(v => (Guid?)v.Id).SingleOrDefaultAsync(Ct);
    }

    private async Task<long> AuditCountAsync(string action, string outcome)
    {
        var sql = IsSqlServer
            ? $"SELECT COUNT(*) FROM [audit].[AuditEvent] WHERE [Action] = '{action}' AND [Outcome] = '{outcome}'"
            : $"SELECT COUNT(*) FROM audit.audit_event WHERE action = '{action}' AND outcome = '{outcome}'";
        return Convert.ToInt64(await db.ScalarAsync(db.AuditAppConnectionString, sql, Ct), CultureInfo.InvariantCulture);
    }

    private PolicyDbContext Context(string actor) =>
        TestSupport.CreatePolicy(db.Provider, db.PolicyAppConnectionString, new AuditingSaveChangesInterceptor(TimeProvider.System, new FixedActor(actor)));

    private static ApprovalMatrixSelector Selector(PolicyDbContext context, FakeAttributes catalog) => new(context, new MatrixCompiler(catalog));

    private Scope Service(string actor, FakeAttributes catalog, FakePermissions? permissions = null)
    {
        var context = Context(actor);
        var writer = new AuditWriter(
            TestSupport.ProviderFor(db.Provider), TimeProvider.System, new FixedActor(actor), new AuditActionCatalog(MatrixAuditActions.All.Select(a => new AuditActionRegistration(a))));
        return new Scope(context, new ApprovalMatrixService(
            context, writer, TimeProvider.System, TestSupport.ProviderFor(db.Provider), new FixedActor(actor), permissions ?? DefaultPermissions, new MatrixCompiler(catalog)));
    }

    /// <summary>Nothing is published: only the built-in facts can be used.</summary>
    private sealed class FakeAttributes : IAttributeCatalog
    {
        public Task<PublishedAttributeDefinition?> FindPublishedByKeyAsync(string key, CancellationToken cancellationToken) => Task.FromResult<PublishedAttributeDefinition?>(null);

        public Task<PublishedAttributeDefinition?> FindPublishedByIdAsync(Guid attributeDefinitionId, CancellationToken cancellationToken) => Task.FromResult<PublishedAttributeDefinition?>(null);
    }

    private sealed class FakePermissions(params (string Person, string Action)[] grants) : IPermissionEvaluator
    {
        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken) =>
            Task.FromResult(grants.Contains((subject, action)));
    }

    private sealed class Scope(PolicyDbContext context, ApprovalMatrixService service) : IAsyncDisposable
    {
        public ApprovalMatrixService Service { get; } = service;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
