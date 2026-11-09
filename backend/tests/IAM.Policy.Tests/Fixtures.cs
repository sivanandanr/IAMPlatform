using System.Text.Json;
using IAM.Modules.Policy.Domain.Rules;

namespace IAM.Policy.Tests;

/// <summary>Synthetic attributes, rule documents and snapshots. Nothing here is a real policy or real HR data.</summary>
internal static class Fixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    public static readonly AttributeSpec Company = Spec(1, RuleDataType.Code, maxLength: 40);
    public static readonly AttributeSpec Department = Spec(2, RuleDataType.Code, maxLength: 40);
    public static readonly AttributeSpec Subgroup = Spec(3, RuleDataType.Code, maxLength: 10);
    public static readonly AttributeSpec CostCenter = Spec(4, RuleDataType.Code, maxLength: 20);

    /// <summary>Integer here to match the spec's <c>JobGrade &gt;= 7</c>; DL-012 is still open, and the engine follows whatever the catalog declares.</summary>
    public static readonly AttributeSpec JobGrade = Spec(5, RuleDataType.Integer);
    public static readonly AttributeSpec HireDate = Spec(6, RuleDataType.Date);
    public static readonly AttributeSpec Band = Spec(7, RuleDataType.Code, maxLength: 20, sensitivity: RuleSensitivity.Restricted);
    public static readonly AttributeSpec Salary = Spec(8, RuleDataType.Decimal, sensitivity: RuleSensitivity.Confidential);
    public static readonly AttributeSpec Active = Spec(9, RuleDataType.Boolean);
    public static readonly AttributeSpec LastSeen = Spec(10, RuleDataType.Timestamp);
    public static readonly AttributeSpec Title = Spec(11, RuleDataType.Text, maxLength: 100);
    public static readonly AttributeSpec Upper = Spec(12, RuleDataType.Code, maxLength: 20, normalization: RuleNormalization.TrimUpperInvariant);
    public static readonly AttributeSpec Entitlement = Spec(13, RuleDataType.Code, maxLength: 20, scope: RuleSubject.Entitlement);

    public static readonly IReadOnlyDictionary<Guid, AttributeSpec> Catalog =
        new[] { Company, Department, Subgroup, CostCenter, JobGrade, HireDate, Band, Salary, Active, LastSeen, Title, Upper, Entitlement }
            .ToDictionary(a => a.AttributeId);

    public static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    public static AttributeSpec Spec(
        int id,
        RuleDataType type,
        int? maxLength = null,
        RuleSensitivity sensitivity = RuleSensitivity.Internal,
        RuleNormalization normalization = RuleNormalization.None,
        RuleSubject scope = RuleSubject.Beneficiary,
        int freshnessMinutes = 60) =>
        new(Id(id), Id(1000 + id), $"hash-{id}", scope, type, sensitivity, normalization, freshnessMinutes, maxLength, RuleVocabulary.AllowedOperators(type).ToHashSet());

    public static string Doc(string root) => $$"""{"schemaVersion":1,"root":{{root}}}""";

    public static string Cond(AttributeSpec attribute, string op, params string[] values) => Cond(RuleSubject.Beneficiary, attribute, op, values);

    public static string Cond(RuleSubject subject, AttributeSpec attribute, string op, params string[] values)
    {
        var list = op is "IsEmpty" or "IsNotEmpty" ? string.Empty : $",\"values\":{JsonSerializer.Serialize(values)}";
        return $$"""{"type":"condition","subject":"{{subject}}","attributeId":"{{attribute.AttributeId:D}}","operator":"{{op}}"{{list}}}""";
    }

    public static string Any(AttributeSpec attribute) =>
        $$"""{"type":"wildcard","subject":"Beneficiary","attributeId":"{{attribute.AttributeId:D}}"}""";

    public static string And(params string[] children) => Group("And", children);

    public static string Or(params string[] children) => Group("Or", children);

    public static string Group(string op, params string[] children) =>
        $$"""{"type":"group","operator":"{{op}}","children":[{{string.Join(',', children)}}]}""";

    public static RuleDefinition Rule(int id, int priority, string root, bool wildcard = false, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        new(Id(id), priority, from, to, wildcard, Doc(root));

    public static CompiledPolicy CompilePolicy(params RuleDefinition[] rules) =>
        PolicyCompiler.Compile(new PolicyVersionDefinition(Id(500), rules), Catalog);

    public static SnapshotEntry Value(AttributeSpec attribute, string? value, DateTimeOffset? at = null, RuleSubject subject = RuleSubject.Beneficiary) =>
        new(subject, attribute.AttributeId, value, at ?? Now.AddMinutes(-5));

    public static AttributeSnapshot Snapshot(params SnapshotEntry[] entries) => new(entries);

    public static EvaluationResult Evaluate(CompiledPolicy policy, params SnapshotEntry[] entries) =>
        PolicyEvaluator.Evaluate(policy, Snapshot(entries), Now);
}
