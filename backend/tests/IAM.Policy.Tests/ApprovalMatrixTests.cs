using IAM.Modules.Policy.Contracts;
using IAM.Modules.Policy.Domain.Matrix;
using IAM.Modules.Policy.Domain.Rules;
using Shouldly;
using static IAM.Policy.Tests.Fixtures;

namespace IAM.Policy.Tests;

/// <summary>The stored approval matrix without a database (DL-090, DL-011, CLAUDE.md rules 1, 3, 4 and 8).</summary>
public sealed class ApprovalMatrixTests
{
    private static readonly Guid PrivilegedRule = Id(101);
    private static readonly Guid DefaultRule = Id(102);

    internal static string Stage(int sequence = 1, string kind = "Decision", string source = "EntitlementOwner", string? role = null, string? workgroup = null,
        string quorum = "AnyOne", int? minimum = null, bool mandatory = false, bool independent = false, int hours = 72, string timeout = "Escalate", string fields = "[]") =>
        $$"""{"sequence":{{sequence}},"kind":"{{kind}}","source":"{{source}}","roleTypeCode":{{Text(role)}},"workgroupKey":{{Text(workgroup)}},"quorum":"{{quorum}}","minimumApprovals":{{(minimum is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}},"mandatory":{{Bool(mandatory)}},"independent":{{Bool(independent)}},"deadlineHours":{{hours}},"onTimeout":"{{timeout}}","fields":{{fields}}}""";

    internal static string RuleJson(Guid id, int priority, string condition, string route, bool wildcard = false) =>
        $$"""{"ruleId":"{{id:D}}","priority":{{priority}},"effectiveFromUtc":null,"effectiveToUtc":null,"allowsWildcard":{{Bool(wildcard)}},"condition":{{condition}},"route":[{{route}}]}""";

    internal static string Matrix(params string[] rules) => $$"""{"rules":[{{string.Join(',', rules)}}]}""";

    internal static string Privileged(bool value) =>
        Doc($$"""{"type":"condition","subject":"Entitlement","attributeId":"{{BuiltInFacts.EntitlementPrivileged.AttributeId:D}}","operator":"Equals","values":["{{(value ? "true" : "false")}}"]}""");

    internal static string AnyApplication() =>
        Doc($$"""{"type":"wildcard","subject":"Application","attributeId":"{{BuiltInFacts.ApplicationKey.AttributeId:D}}"}""");

    /// <summary>Privileged entitlements: owner, then a mandatory independent IAM workgroup. Everything else: the owner.</summary>
    internal static string Standard() => Matrix(
        RuleJson(PrivilegedRule, 200, Privileged(true), Stage() + "," + Stage(2, source: "Workgroup", workgroup: "iam_security", mandatory: true, independent: true)),
        RuleJson(DefaultRule, 100, AnyApplication(), Stage(), wildcard: true));

    private static IReadOnlyDictionary<Guid, AttributeSpec> Facts => BuiltInFacts.All.ToDictionary(f => f.Spec.AttributeId, f => f.Spec);

    [Fact]
    public void TheBuiltInFactIdsMatchTheContract()
    {
        BuiltInFacts.RequestAction.AttributeId.ShouldBe(BuiltInFactIds.RequestAction);
        BuiltInFacts.RequestAccountType.AttributeId.ShouldBe(BuiltInFactIds.RequestAccountType);
        BuiltInFacts.RequestNewAccount.AttributeId.ShouldBe(BuiltInFactIds.RequestNewAccount);
        BuiltInFacts.EntitlementKey.AttributeId.ShouldBe(BuiltInFactIds.EntitlementKey);
        BuiltInFacts.EntitlementPrivileged.AttributeId.ShouldBe(BuiltInFactIds.EntitlementPrivileged);
        BuiltInFacts.EntitlementRisk.AttributeId.ShouldBe(BuiltInFactIds.EntitlementRisk);
        BuiltInFacts.EntitlementType.AttributeId.ShouldBe(BuiltInFactIds.EntitlementType);
        BuiltInFacts.ApplicationKey.AttributeId.ShouldBe(BuiltInFactIds.ApplicationKey);
        BuiltInFacts.All.Select(f => f.Key).ShouldBeUnique();
    }

    [Fact]
    public void ADocumentIsReadStrictlyAndStoredCanonically()
    {
        var document = MatrixDocuments.Parse(Standard());
        var stored = MatrixDocuments.ToStored(document);
        MatrixDocuments.ToStored(MatrixDocuments.Parse(stored)).ShouldBe(stored);
        var second = document.Rules[0].Route[1];
        IReadOnlyList<MatrixActionField> none = [];
        (second with { Fields = none }).ShouldBe(new MatrixStage(2, MatrixStageKind.Decision, MatrixApproverSource.Workgroup, null, "iam_security", MatrixQuorum.AnyOne, null, true, true, 72, MatrixTimeout.Escalate, none));
        document.Rules[0].Route[1].Fields.ShouldBeEmpty();
        stored.ShouldBe(Standard(), "the generator writes the canonical form");

        foreach (var bad in new[]
        {
            "not json",
            """{"rules":[]}""",
            """{"rules":[],"extra":1}""",
            Standard().Replace("\"priority\":200", "\"priority\":200,\"priority\":300", StringComparison.Ordinal),
            Standard().Replace("\"deadlineHours\":72", "\"deadlineHours\":\"72\"", StringComparison.Ordinal),
            Standard().Replace("\"kind\":\"Decision\"", "\"kind\":\"decision\"", StringComparison.Ordinal),
            Standard().Replace("\"onTimeout\":\"Escalate\"", "\"onTimeout\":\"Approve\"", StringComparison.Ordinal),
            Standard().Replace(",\"fields\":[]", string.Empty, StringComparison.Ordinal),
            Standard().Replace("\"ruleId\":\"" + PrivilegedRule.ToString("D"), "\"ruleId\":\"" + DefaultRule.ToString("D"), StringComparison.Ordinal),
        })
        {
            Should.Throw<MatrixRejectedException>(() => MatrixDocuments.Validate(MatrixDocuments.Parse(bad)), bad.Length > 60 ? bad[..60] : bad);
        }
    }

    [Fact]
    public void EveryRouteNeedsADecisionAndActionStagesAreNameTasksOnly()
    {
        void Refused(string route) =>
            Should.Throw<MatrixRejectedException>(() => MatrixDocuments.Validate(MatrixDocuments.Parse(Matrix(RuleJson(DefaultRule, 1, AnyApplication(), route, wildcard: true)))), route);

        Refused(Stage(kind: "Action", fields: "[\"AccountName\"]"));
        Refused(Stage() + "," + Stage(2, kind: "Action", fields: "[]"));
        Refused(Stage() + "," + Stage(2, kind: "Action", mandatory: true, fields: "[\"AccountName\"]"));
        Refused(Stage() + "," + Stage(2, kind: "Action", quorum: "All", fields: "[\"AccountName\"]"));
        Refused(Stage() + "," + Stage(2, kind: "Action", timeout: "Reject", fields: "[\"AccountName\"]"));
        Refused(Stage(fields: "[\"AccountName\"]"));
        Refused(Stage(fields: "[\"Password\"]"));
        Refused(Stage(source: "Workgroup"));
        Refused(Stage(source: "ApplicationRole"));
        Refused(Stage(source: "EntitlementOwner", workgroup: "grp_x"));
        Refused(Stage(source: "Workgroup", workgroup: "Not A Key"));
        Refused(Stage(quorum: "MinimumN"));
        Refused(Stage(quorum: "AnyOne", minimum: 2));
        Refused(Stage(hours: 0));
        Refused(Stage(hours: MatrixDocuments.MaxDeadlineHours + 1));
        Refused(Stage(sequence: 0));
        Refused(string.Join(',', Enumerable.Range(1, MatrixDocuments.MaxStages + 1).Select(i => Stage(i))));

        Should.NotThrow(() => MatrixDocuments.Validate(MatrixDocuments.Parse(Matrix(RuleJson(DefaultRule, 1, AnyApplication(),
            Stage(kind: "Action", fields: "[\"AccountName\"]") + "," + Stage(2, source: "ApplicationRole", role: "it_admin", quorum: "MinimumN", minimum: 2), wildcard: true)))));
    }

    [Fact]
    public void ConditionsCompileAgainstTheFactsAndUnknownAttributesAreRefused()
    {
        var compiled = MatrixDocuments.Compile(Guid.Empty, MatrixDocuments.Parse(Standard()), Facts);
        compiled.Publishable.ShouldBeTrue();
        compiled.Routes[PrivilegedRule].Count.ShouldBe(2);

        var unknown = Matrix(RuleJson(DefaultRule, 1, Doc(Cond(Company, "Equals", "ABC")), Stage()));
        Should.Throw<MatrixRejectedException>(() => MatrixDocuments.Compile(Guid.Empty, MatrixDocuments.Parse(unknown), Facts));
        MatrixDocuments.AttributeIds(MatrixDocuments.Parse(unknown)).ShouldBe([Company.AttributeId]);

        // With the published attribute added to the catalog, the same document compiles; the hash covers its version.
        var withCompany = new Dictionary<Guid, AttributeSpec>(Facts) { [Company.AttributeId] = Company };
        var first = MatrixDocuments.Compile(Guid.Empty, MatrixDocuments.Parse(unknown), withCompany).Policy.ContentHash;
        withCompany[Company.AttributeId] = Company with { VersionId = Id(999) };
        MatrixDocuments.Compile(Guid.Empty, MatrixDocuments.Parse(unknown), withCompany).Policy.ContentHash.ShouldNotBe(first);
    }

    [Fact]
    public void PossibleOverlapsMakeAVersionUnpublishable()
    {
        var overlapping = Matrix(
            RuleJson(PrivilegedRule, 100, Privileged(true), Stage()),
            RuleJson(DefaultRule, 100, AnyApplication(), Stage(), wildcard: true));
        var compiled = MatrixDocuments.Compile(Guid.Empty, MatrixDocuments.Parse(overlapping), Facts);
        compiled.Analysis.Overlaps.ShouldNotBeEmpty();
        compiled.Publishable.ShouldBeFalse();

        var matrix = ApprovalMatrix.Create(MatrixDocuments.Parse(overlapping), compiled.Policy.ContentHash, "author");
        Should.Throw<MatrixRefusedException>(() => MatrixChangeRequest.Propose(Guid.CreateVersion7(), matrix, compiled.Publishable, "maker", "Ready", Now))
            .Refusal.ShouldBe(MatrixRefusal.OverlapsFound);
    }

    [Fact]
    public void ThePriorityMatchSelectsTheRouteAndUnknownFactsHold()
    {
        var compiled = MatrixDocuments.Compile(Guid.Empty, MatrixDocuments.Parse(Standard()), Facts);
        EvaluationResult Run(params SnapshotEntry[] facts) => PolicyEvaluator.Evaluate(compiled.Policy, new AttributeSnapshot(facts), Now);
        var app = new SnapshotEntry(RuleSubject.Application, BuiltInFacts.ApplicationKey.AttributeId, "erp", Now);

        var privileged = Run(app, new SnapshotEntry(RuleSubject.Entitlement, BuiltInFacts.EntitlementPrivileged.AttributeId, "true", Now));
        privileged.Outcome.ShouldBe(RuleOutcome.Matched);
        privileged.MatchedRuleId.ShouldBe(PrivilegedRule);

        Run(app, new SnapshotEntry(RuleSubject.Entitlement, BuiltInFacts.EntitlementPrivileged.AttributeId, "false", Now)).MatchedRuleId.ShouldBe(DefaultRule);

        // Not knowing whether it is privileged never falls through to the cheaper route (DL-009).
        Run(app).Outcome.ShouldBe(RuleOutcome.HoldMissingOrStaleData);
        Run(app, new SnapshotEntry(RuleSubject.Entitlement, BuiltInFacts.EntitlementPrivileged.AttributeId, "true", Now.AddHours(-1))).Outcome
            .ShouldBe(RuleOutcome.HoldMissingOrStaleData, "a fact older than five minutes is stale");
    }

    [Fact]
    public void AVersionIsActivatedBySomeoneWhoNeitherProposedNorWroteIt()
    {
        var document = MatrixDocuments.Parse(Standard());
        var hash = MatrixDocuments.Compile(Guid.Empty, document, Facts).Policy.ContentHash;
        var matrix = ApprovalMatrix.Create(document, hash, "author");
        var request = MatrixChangeRequest.Propose(Guid.CreateVersion7(), matrix, true, "maker", "Ready", Now);

        Should.Throw<MatrixRefusedException>(() => request.Approve("maker", null, matrix, hash, Now)).Refusal.ShouldBe(MatrixRefusal.MakerCannotDecide);
        Should.Throw<MatrixRefusedException>(() => request.Approve("author", null, matrix, hash, Now)).Refusal.ShouldBe(MatrixRefusal.MakerCannotDecide);
        Should.Throw<MatrixRefusedException>(() => request.Approve("checker", null, matrix, new string('0', 64), Now)).Refusal.ShouldBe(MatrixRefusal.AttributesChanged);

        request.Approve("checker", null, matrix, hash, Now);
        matrix.CurrentActive.ShouldNotBeNull().ActivatedByRequestId.ShouldBe(request.Id);

        // A new draft supersedes it only when approved; an edit after proposing makes the request stale.
        var next = MatrixDocuments.Parse(Standard().Replace("\"deadlineHours\":72", "\"deadlineHours\":48", StringComparison.Ordinal));
        matrix.SetDraft(next, hash, 1, "author").ShouldBeTrue();
        var second = MatrixChangeRequest.Propose(Guid.CreateVersion7(), matrix, true, "maker", "Faster", Now);
        matrix.SetDraft(MatrixDocuments.Parse(Standard()), hash, 2, "author");
        Should.Throw<MatrixRefusedException>(() => second.Approve("checker", null, matrix, hash, Now)).Refusal.ShouldBe(MatrixRefusal.DraftChanged);
    }

    [Fact]
    public void AStoredDocumentThatIsNotCanonicalIsDamaged()
    {
        var document = MatrixDocuments.Parse(Standard());
        var matrix = ApprovalMatrix.Create(document, MatrixDocuments.Compile(Guid.Empty, document, Facts).Policy.ContentHash, "author");
        matrix.CurrentDraft!.ToDocument().Rules.Count.ShouldBe(2);
        typeof(MatrixVersion).GetProperty(nameof(MatrixVersion.Document))!.SetValue(matrix.CurrentDraft, Standard().Replace("{\"rules\":", "{ \"rules\": ", StringComparison.Ordinal));
        Should.Throw<MatrixRefusedException>(() => matrix.CurrentDraft.ToDocument()).Refusal.ShouldBe(MatrixRefusal.InvalidRecord);
    }

    private static string Text(string? value) => value is null ? "null" : $"\"{value}\"";

    private static string Bool(bool value) => value ? "true" : "false";
}
