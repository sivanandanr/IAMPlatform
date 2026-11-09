using System.Globalization;
using IAM.Modules.Identity.Domain.Attributes;
using IAM.Modules.Policy.Domain.Rules;
using Shouldly;
using static IAM.Policy.Tests.Fixtures;

namespace IAM.Policy.Tests;

/// <summary>Regression tests for the S2-01 independent review findings. Each was confirmed against the code first.</summary>
public sealed class ReviewRegressionTests
{
    private static readonly string Slash = ((char)92).ToString();

    private static RuleRejection? Rejection(params RuleDefinition[] rules) =>
        Should.Throw<RuleRejectedException>(() => CompilePolicy(rules)).Rejection;

    private static string HashOf(params RuleDefinition[] rules) => CompilePolicy(rules).ContentHash;

    [Fact]
    public void EscapedLoneSurrogatesAreRejectedNotThrownAsAnotherException()
    {
        var surrogate = Slash + "uD800";
        var id = Company.AttributeId.ToString("D");
        foreach (var json in new[]
        {
            Doc($$"""{"type":"{{surrogate}}"}"""),
            "{\"schemaVersion\":1,\"" + surrogate + "\":1,\"root\":{}}",
            Doc($$"""{"type":"condition","subject":"Beneficiary","attributeId":"{{id}}","operator":"In","values":["{{surrogate}}"]}"""),
            Doc($$"""{"type":"condition","subject":"Beneficiary","attributeId":"{{surrogate}}","operator":"In","values":["A"]}"""),
        })
        {
            Should.Throw<RuleRejectedException>(() => RuleDocumentParser.Parse(json)).Rejection.ShouldBe(RuleRejection.InvalidJson);
        }
    }

    [Fact]
    public void DuplicatesAreFoundAfterUnescaping()
    {
        var escaped = "operat" + Slash + "u006fr";
        var node = $$"""{"type":"condition","subject":"Beneficiary","attributeId":"{{Company.AttributeId:D}}","operator":"Equals","{{escaped}}":"NotEquals","values":["A"]}""";
        Should.Throw<RuleRejectedException>(() => RuleDocumentParser.Parse(Doc(node))).Rejection.ShouldBe(RuleRejection.DuplicateProperty);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"root":{"secret-value"}}""")]
    [InlineData("secret-value")]
    public void RejectionsCarryNoInnerExceptionThatCouldQuoteTheInput(string json)
    {
        var rejection = Should.Throw<RuleRejectedException>(() => RuleDocumentParser.Parse(json));
        rejection.InnerException.ShouldBeNull();
        rejection.ToString().ShouldNotContain("secret-value");
    }

    [Fact]
    public void TextAttributesAreRefusedInRules()
    {
        Rejection(Rule(1, 1, Cond(Title, "Equals", "Finance"))).ShouldBe(RuleRejection.AttributeTypeNotAllowed);
        Rejection(Rule(1, 1, Cond(Title, "NotEquals", "Finance"))).ShouldBe(RuleRejection.AttributeTypeNotAllowed);
        Rejection(Rule(1, 1, Cond(Title, "IsNotEmpty"))).ShouldBe(RuleRejection.AttributeTypeNotAllowed);
        Rejection(Rule(1, 1, Any(Title), wildcard: true)).ShouldBe(RuleRejection.AttributeTypeNotAllowed);
    }

    [Fact]
    public void LongSIsNotAValidCodeInEitherParser()
    {
        var longS = (char)0x17F + "abc";
        AttributeValueCodec.TryParse(Upper, longS, out _).ShouldBeFalse();
        var draft = new AttributeDraft("Name", AttributeDataType.Code, AttributeSubjectScope.Beneficiary, "hr", AttributeSensitivity.Internal, AttributeNormalization.TrimUpperInvariant, 60, 20, [AttributeOperator.Equals]);
        AttributeValueValidator.Validate(draft, longS).IsValid.ShouldBeFalse();
        AttributeValueCodec.TryParse(Upper, " abc ", out var plain).ShouldBeTrue();
        plain.Canonical.ShouldBe("ABC");
    }

    [Fact]
    public void ACatalogEntryMustBeForTheIdItIsFiledUnder()
    {
        var catalog = new Dictionary<Guid, AttributeSpec> { [Company.AttributeId] = Department };
        var version = new PolicyVersionDefinition(Id(500), [Rule(1, 1, Cond(Company, "Equals", "A"))]);
        Should.Throw<RuleRejectedException>(() => PolicyCompiler.Compile(version, catalog)).Rejection.ShouldBe(RuleRejection.UnknownAttribute);
    }

    [Fact]
    public void AnUnknownAttributeInAnAllowedWildcardIsRefused() =>
        Rejection(Rule(1, 1, Any(Spec(99, RuleDataType.Code, maxLength: 5)), wildcard: true)).ShouldBe(RuleRejection.UnknownAttribute);

    [Fact]
    public void ARequesterScopedAttributeCannotBeReadForTheBeneficiary()
    {
        var requester = Spec(70, RuleDataType.Code, maxLength: 5, scope: RuleSubject.Requester);
        var catalog = new Dictionary<Guid, AttributeSpec> { [requester.AttributeId] = requester };
        var version = new PolicyVersionDefinition(Id(500), [Rule(1, 1, Cond(RuleSubject.Beneficiary, requester, "Equals", "A"))]);
        Should.Throw<RuleRejectedException>(() => PolicyCompiler.Compile(version, catalog)).Rejection.ShouldBe(RuleRejection.SubjectNotAllowed);
    }

    [Fact]
    public void ValueCountEdgesAreChecked()
    {
        Rejection(Rule(1, 1, Cond(JobGrade, "GreaterThan", "1", "2"))).ShouldBe(RuleRejection.WrongValueCount);
        var emptyList = $$"""{"type":"condition","subject":"Beneficiary","attributeId":"{{Company.AttributeId:D}}","operator":"IsEmpty","values":[]}""";
        Should.NotThrow(() => CompilePolicy(Rule(1, 1, emptyList)));
        var nullList = $$"""{"type":"condition","subject":"Beneficiary","attributeId":"{{Company.AttributeId:D}}","operator":"In","values":null}""";
        Rejection(Rule(1, 1, nullList)).ShouldBe(RuleRejection.InvalidPropertyType);
        Rejection(Rule(1, 1, Cond(Company, "Equals", "A")), null!).ShouldBe(RuleRejection.EmptyPolicy);
    }

    [Fact]
    public void AFixedPolicyAlwaysHashesToTheSameValue()
    {
        // A serializer or canonical-form change must not silently invalidate stored hashes (ADR-0004, ADR-0017).
        var hash = HashOf(
            Rule(1, 10, And(Cond(Subgroup, "In", "2", "01"), Or(Cond(JobGrade, "GreaterThanOrEqual", "7"), Any(Company))), wildcard: true, from: Now, to: Now.AddDays(1)));
        hash.ShouldBe(ExpectedHash);
    }

    private const string ExpectedHash = "52e16f088216fbc563073988547427487665ede37284cd75e05888bf25c48d8d";

    [Fact]
    public void TheHashCoversEveryFieldThatChangesADecision()
    {
        var node = Cond(Company, "Equals", "A");
        var baseline = HashOf(Rule(1, 1, node, from: Now, to: Now.AddDays(1)));

        HashOf(Rule(1, 1, node, from: Now.AddTicks(1), to: Now.AddDays(1))).ShouldNotBe(baseline);
        HashOf(Rule(1, 1, node, from: Now, to: Now.AddDays(1).AddTicks(1))).ShouldNotBe(baseline);

        var and = HashOf(Rule(1, 1, And(node, Cond(Department, "Equals", "B"))));
        HashOf(Rule(1, 1, Or(node, Cond(Department, "Equals", "B")))).ShouldNotBe(and);
        HashOf(Rule(1, 1, And(node, And(Cond(Department, "Equals", "B"))))).ShouldNotBe(and);
        HashOf(Rule(1, 1, Any(Company), wildcard: true)).ShouldNotBe(HashOf(Rule(1, 1, Cond(Company, "IsNotEmpty"))));
    }

    public static TheoryData<string> AttributeChanges() => ["Freshness", "Normalization", "Sensitivity", "MaxLength", "Scope", "Operators"];

    [Theory]
    [MemberData(nameof(AttributeChanges))]
    public void TheHashChangesWithAttributeBehaviourEvenIfTheCatalogHashDoesNot(string field)
    {
        var rule = Rule(1, 1, Cond(Company, "Equals", "A"));
        var changed = field switch
        {
            "Freshness" => Company with { FreshnessMaxAgeMinutes = 999 },
            "Normalization" => Company with { Normalization = RuleNormalization.Trim },
            "Sensitivity" => Company with { Sensitivity = RuleSensitivity.Restricted },
            "MaxLength" => Company with { MaxLength = 39 },
            "Scope" => Company with { Scope = RuleSubject.Requester, Operators = Company.Operators },
            _ => Company with { Operators = new HashSet<RuleOperator> { RuleOperator.Equals } },
        };
        var catalog = new Dictionary<Guid, AttributeSpec> { [changed.AttributeId] = changed };
        if (field == "Scope")
        {
            rule = Rule(1, 1, Cond(RuleSubject.Requester, Company, "Equals", "A"));
            HashOf(rule).ShouldNotBe(PolicyCompiler.Compile(new PolicyVersionDefinition(Id(500), [rule]), catalog).ContentHash);
            return;
        }

        PolicyCompiler.Compile(new PolicyVersionDefinition(Id(500), [rule]), catalog).ContentHash.ShouldNotBe(HashOf(rule));
    }

    [Fact]
    public void EvidenceKeepsFullTimePrecisionSoReplayUsesTheSameMoment()
    {
        var policy = CompilePolicy(Rule(1, 1, Cond(Company, "Equals", "A"), from: Now.AddTicks(1)));
        var result = PolicyEvaluator.Evaluate(policy, Snapshot(Value(Company, "A")), Now.AddTicks(1));
        result.Outcome.ShouldBe(RuleOutcome.Matched);
        result.Evidence.ToCanonicalJson().ShouldContain("2026-11-09T08:00:00.0000001Z");
    }

    [Fact]
    public void CompiledTypesCannotBeBuiltOrAlteredFromOutside()
    {
        typeof(CompiledPolicy).GetConstructors().ShouldBeEmpty();
        typeof(CompiledRule).GetConstructors().ShouldBeEmpty();
        typeof(CompiledPolicy).IsSealed.ShouldBeTrue();
        typeof(CompiledPolicy).GetMethods().ShouldNotContain(m => m.Name == "<Clone>$");
        typeof(RuleDocumentParser).IsPublic.ShouldBeFalse();

        var policy = CompilePolicy(Rule(1, 1, Cond(Company, "Equals", "A")));
        ((ICollection<CompiledRule>)policy.Rules).IsReadOnly.ShouldBeTrue();
        ((ICollection<AttributeSpec>)policy.Attributes).IsReadOnly.ShouldBeTrue();
    }

    [Fact]
    public void TheStepBudgetIsExactlyWhatAMaximalVersionNeeds()
    {
        var leaf = Cond(Company, "Equals", "NOPE");
        var root = And(Enumerable.Repeat(And(And(And(leaf))), RuleLimits.MaxConditionsPerRule).ToArray());
        var policy = CompilePolicy([.. Enumerable.Range(1, RuleLimits.MaxRulesPerVersion).Select(i => Rule(i, i, root))]);
        var snapshot = Snapshot(Value(Company, "ABC"));

        RuleLimits.MaxEvaluationSteps.ShouldBe(129_000);
        PolicyEvaluator.Evaluate(policy, snapshot, Now).Outcome.ShouldBe(RuleOutcome.HoldNoMatch);
        PolicyEvaluator.Evaluate(policy, snapshot, Now, 128_999).Outcome.ShouldBe(RuleOutcome.HoldBudgetExceeded);
    }

    [Fact]
    public void LoggedRecordsDoNotShowValues()
    {
        const string Secret = "RESTRICTED-VALUE";
        Value(Band, Secret).ToString().ShouldNotContain(Secret);
        Rule(1, 1, Cond(Band, "Equals", Secret)).ToString().ShouldNotContain(Secret);
        AttributeValueCodec.TryParse(Band, Secret, out var parsed).ShouldBeTrue();
        parsed.ToString().ShouldNotContain(Secret);
    }

    [Fact]
    public void HashAndEvidenceDoNotDependOnTheMachineCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        string Run()
        {
            var policy = CompilePolicy(Rule(1, 1, And(Cond(Upper, "Equals", "title"), Cond(Salary, "GreaterThan", "1.5"), Cond(HireDate, "LessThan", "2030-01-01")), from: Now));
            return policy.ContentHash + Evaluate(policy, Value(Upper, "title"), Value(Salary, "2.5"), Value(HireDate, "2026-01-01")).Evidence.ToCanonicalJson();
        }

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = Run();
            foreach (var name in new[] { "tr-TR", "de-DE", "ar-EG", "th-TH" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Run().ShouldBe(invariant);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void ValueParsingAgreesWithTheCatalogOnAWideCorpus()
    {
        AttributeValueCodec.MaxRawLength.ShouldBe(AttributeValueValidator.MaxRawLength);
        string[] corpus =
        [
            "1", "01", "001", "-1", "-0", "0", "abc", "ABC", " abc ", "a b", "a/b-c.d_e", "-abc", "_a", ".a", "1.50", "1.5", "-0.0", "1.", ".5", "-", "--1", "+1",
            "12345678901234567890.12345678", "99999999999999999999.99999999", "123456789012345678901", "1.123456789",
            "999999999999999999", "-999999999999999999", "9999999999999999999", "9223372036854775807", "79228162514264337593543950335",
            "1e3", "NaN", "Infinity", "true", "false", "True", "TRUE", "2026-02-28", "2024-02-29", "2026-02-30", "2026-2-1", "0000-01-01", "9999-12-31",
            "2026-11-09T08:00:00Z", "2026-11-09T08:00:00.1Z", "2026-11-09T08:00:00.123456Z", "2026-11-09T08:00:00.1234567Z", "2026-11-09T08:00:00+01:00",
            "2026-11-09t08:00:00z", "2026-11-09T24:00:00Z", "2026-11-09T08:00:00", "2026-11-09T08:00:00.Z", "2026-13-09T08:00:00Z",
            string.Empty, " ", "   ", "\t", "<b>", new string('x', 10), new string('x', 11), new string('x', 400),
            "1\n", "abc\n", "2026-02-28\n", "true\n", "2026-11-09T08:00:00Z\n", "a" + (char)1 + "b",
            "a" + (char)0x200B + "b", "a" + (char)0xE000 + "b", "a" + (char)0x2028 + "b", char.ConvertFromUtf32(0x1F600),
            ((char)0x0663).ToString(), "1" + (char)0x0663, ((char)0xFF11).ToString(), (char)0x17F + "abc", (char)0x212A + "abc", (char)0xDF + "abc", (char)0xE9 + "x",
            "e" + (char)0x301, (char)0xA0 + "abc", "abc" + (char)0xA0, ((char)0x131).ToString(), "i", "I",
            new string(' ', 1599) + "1", new string(' ', 1600) + "1", new string('x', 1600), new string('x', 1601),
        ];

        var checkedValues = 0;
        foreach (var dataType in Enum.GetValues<AttributeDataType>())
        {
            foreach (var normalization in Enum.GetValues<AttributeNormalization>())
            {
                var stringType = AttributeRules.IsStringType(dataType);
                var draft = new AttributeDraft(
                    "Name", dataType, AttributeSubjectScope.Beneficiary, "hr", AttributeSensitivity.Internal,
                    normalization, 60, stringType ? 10 : null, [AttributeRules.AllowedOperators(dataType)[0]]);
                var spec = new AttributeSpec(
                    Id(1), Id(2), "h", RuleSubject.Beneficiary, Enum.Parse<RuleDataType>(dataType.ToString()), RuleSensitivity.Internal,
                    Enum.Parse<RuleNormalization>(normalization.ToString()), 60, stringType ? 10 : null, new HashSet<RuleOperator>());

                foreach (var raw in corpus)
                {
                    var catalog = AttributeValueValidator.Validate(draft, raw);
                    var parsed = AttributeValueCodec.TryParse(spec, raw, out var value);
                    var label = $"{dataType}/{normalization}/{checkedValues}";
                    parsed.ShouldBe(catalog.IsValid, label);
                    if (parsed)
                    {
                        value.Canonical.ShouldBe(catalog.Canonical, label);
                        value.Comparable.ShouldBe(catalog.Value, label);
                    }

                    checkedValues++;
                }
            }
        }

        checkedValues.ShouldBeGreaterThan(1_000);
    }
}

/// <summary>Range, contradiction and truncation behaviour of the pre-publication check.</summary>
public sealed class RuleAnalysisTests
{
    private static RuleAnalysis Analyze(params RuleDefinition[] rules) => RuleOverlapAnalyzer.Analyze(CompilePolicy(rules));

    public static TheoryData<string, string, string, string, bool> Ranges() => new()
    {
        { "GreaterThan", "7", "LessThan", "3", true },
        { "GreaterThan", "7", "LessThanOrEqual", "7", true },
        { "GreaterThanOrEqual", "7", "LessThan", "7", true },
        { "GreaterThanOrEqual", "7", "LessThanOrEqual", "7", false },
        { "GreaterThan", "7", "LessThan", "9", false },
        { "GreaterThan", "7", "GreaterThan", "9", false },
        { "LessThan", "3", "LessThan", "1", false },
    };

    [Theory]
    [MemberData(nameof(Ranges))]
    public void RangesThatCannotShareAValueAreDisjoint(string op1, string v1, string op2, string v2, bool disjoint)
    {
        var overlaps = Analyze(Rule(1, 50, Cond(JobGrade, op1, v1)), Rule(2, 50, Cond(JobGrade, op2, v2))).Overlaps;
        overlaps.Count.ShouldBe(disjoint ? 0 : 1);
    }

    [Fact]
    public void RepeatedBoundsOnOneAttributeKeepTheTighterOne()
    {
        // Whichever order they are written in: > 2 AND > 8 means > 8, so it cannot share a value with < 5.
        var below5 = Rule(2, 50, Cond(JobGrade, "LessThan", "5"));
        Analyze(Rule(1, 50, And(Cond(JobGrade, "GreaterThan", "2"), Cond(JobGrade, "GreaterThan", "8"))), below5).Overlaps.ShouldBeEmpty();
        Analyze(Rule(1, 50, And(Cond(JobGrade, "GreaterThan", "8"), Cond(JobGrade, "GreaterThan", "2"))), below5).Overlaps.ShouldBeEmpty();

        var above5 = Rule(2, 50, Cond(JobGrade, "GreaterThan", "5"));
        Analyze(Rule(1, 50, And(Cond(JobGrade, "LessThan", "8"), Cond(JobGrade, "LessThan", "2"))), above5).Overlaps.ShouldBeEmpty();
        Analyze(Rule(1, 50, And(Cond(JobGrade, "LessThan", "2"), Cond(JobGrade, "LessThan", "8"))), above5).Overlaps.ShouldBeEmpty();

        // At an equal bound the exclusive one is the tighter: >= 5 AND > 5 cannot share a value with <= 5.
        Analyze(Rule(1, 50, And(Cond(JobGrade, "GreaterThanOrEqual", "5"), Cond(JobGrade, "GreaterThan", "5"))), Rule(2, 50, Cond(JobGrade, "LessThanOrEqual", "5"))).Overlaps.ShouldBeEmpty();
    }

    [Fact]
    public void RangesWorkForDatesDecimalsAndTimestamps()
    {
        Analyze(Rule(1, 50, Cond(HireDate, "LessThan", "2026-01-01")), Rule(2, 50, Cond(HireDate, "GreaterThanOrEqual", "2026-01-01"))).Overlaps.ShouldBeEmpty();
        Analyze(Rule(1, 50, Cond(Salary, "LessThan", "10.5")), Rule(2, 50, Cond(Salary, "GreaterThan", "10.5"))).Overlaps.ShouldBeEmpty();
        Analyze(Rule(1, 50, Cond(LastSeen, "LessThan", "2026-01-01T00:00:00Z")), Rule(2, 50, Cond(LastSeen, "GreaterThan", "2026-01-01T00:00:00Z"))).Overlaps.ShouldBeEmpty();
    }

    [Fact]
    public void ListsAndRangesAreComparedByValueNotText()
    {
        // As text "10" sorts before "9"; as numbers 10 is inside the range.
        Analyze(Rule(1, 50, Cond(JobGrade, "In", "10")), Rule(2, 50, Cond(JobGrade, "GreaterThan", "9"))).Overlaps.ShouldHaveSingleItem();
        Analyze(Rule(1, 50, Cond(JobGrade, "In", "1", "2")), Rule(2, 50, Cond(JobGrade, "GreaterThan", "9"))).Overlaps.ShouldBeEmpty();
        Analyze(Rule(1, 50, Cond(JobGrade, "Equals", "5")), Rule(2, 50, Cond(JobGrade, "NotEquals", "5"))).Overlaps.ShouldBeEmpty();
        Analyze(Rule(1, 50, Cond(JobGrade, "In", "5", "6")), Rule(2, 50, Cond(JobGrade, "NotEquals", "5"))).Overlaps.ShouldHaveSingleItem();
    }

    [Fact]
    public void NotEqualsAloneIsNotDisjoint() =>
        Analyze(Rule(1, 50, Cond(Company, "NotEquals", "A")), Rule(2, 50, Cond(Company, "NotEquals", "B"))).Overlaps.ShouldHaveSingleItem();

    public static TheoryData<string> Contradictions() =>
    [
        And(Cond(Company, "Equals", "A"), Cond(Company, "Equals", "B")),
        And(Cond(Company, "Equals", "A"), Cond(Company, "NotEquals", "A")),
        And(Cond(Subgroup, "In", "1", "2"), Cond(Subgroup, "NotIn", "1", "2")),
        And(Cond(JobGrade, "GreaterThan", "7"), Cond(JobGrade, "LessThan", "3")),
        And(Cond(JobGrade, "GreaterThan", "7"), Cond(JobGrade, "LessThanOrEqual", "7")),
        And(Cond(JobGrade, "Equals", "5"), Cond(JobGrade, "GreaterThan", "5")),
    ];

    [Theory]
    [MemberData(nameof(Contradictions))]
    public void ARuleThatCanNeverBeTrueIsReported(string root)
    {
        var analysis = Analyze(Rule(1, 50, root));
        analysis.NeverTrueRuleIds.ShouldBe([Id(1)]);
        analysis.IsClean.ShouldBeFalse();

        foreach (var (company, subgroup, grade) in new[] { ("A", "1", "5"), ("B", "2", "8"), ("C", "3", "2"), ("A", "2", "7") })
        {
            Evaluate(CompilePolicy(Rule(1, 50, root)), Value(Company, company), Value(Subgroup, subgroup), Value(JobGrade, grade)).Outcome.ShouldNotBe(RuleOutcome.Matched);
        }
    }

    [Fact]
    public void SatisfiableRulesAreNotReportedAsNeverTrue()
    {
        var analysis = Analyze(Rule(1, 50, And(Cond(JobGrade, "GreaterThanOrEqual", "5"), Cond(JobGrade, "LessThanOrEqual", "5"))), Rule(2, 60, Or(Cond(Company, "Equals", "A"), Cond(Company, "Equals", "B"))));
        analysis.NeverTrueRuleIds.ShouldBeEmpty();
        analysis.IsClean.ShouldBeTrue();
    }

    [Fact]
    public void TooManyOverlapsSetATruncatedFlagThatIsNeverClean()
    {
        // 46 identical rules give 1,035 pairs.
        var analysis = Analyze([.. Enumerable.Range(1, 46).Select(i => Rule(i, 50, Cond(Company, "Equals", "A")))]);
        analysis.Overlaps.Count.ShouldBe(RuleLimits.MaxOverlapsReported);
        analysis.Truncated.ShouldBeTrue();
        analysis.IsClean.ShouldBeFalse();

        var exact = Analyze([.. Enumerable.Range(1, 45).Select(i => Rule(i, 50, Cond(Company, "Equals", "A")))]);
        exact.Overlaps.Count.ShouldBe(990);
        exact.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void AnEqualPriorityVersionOfTheL3MatrixIsFlaggedWhereRulesCanBothMatch()
    {
        var finance = And(Cond(Company, "Equals", "ABC"), Cond(Department, "Equals", "Finance"));
        var analysis = Analyze(
            Rule(100, 50, And(finance, Cond(Subgroup, "In", "1", "2"))),
            Rule(110, 50, And(finance, Cond(Subgroup, "In", "3"))),
            Rule(900, 50, finance));
        analysis.Overlaps.Count.ShouldBe(2);
        analysis.Overlaps.ShouldAllBe(o => o.SecondRuleId == Id(900) || o.FirstRuleId == Id(900));
    }

#pragma warning disable CA5394 // A fixed seed on purpose: the generated cases must be the same on every run.
    [Fact]
    public void WheneverTwoRulesAreBothTrueTheCheckFlaggedThem()
    {
        // Soundness: over generated rules and every value in a small domain, a pair it called disjoint is never both true.
        var conditions = new List<string>();
        foreach (var value in new[] { "1", "3", "5", "7", "9" })
        {
            conditions.AddRange([
                Cond(JobGrade, "Equals", value), Cond(JobGrade, "NotEquals", value), Cond(JobGrade, "GreaterThan", value), Cond(JobGrade, "GreaterThanOrEqual", value),
                Cond(JobGrade, "LessThan", value), Cond(JobGrade, "LessThanOrEqual", value), Cond(JobGrade, "In", value, "4"), Cond(JobGrade, "NotIn", value, "4"),
            ]);
        }

        var random = new Random(20261109);
        var ambiguousPairs = 0;
        var disjointPairs = 0;
        for (var round = 0; round < 400; round++)
        {
            string Pick() => conditions[random.Next(conditions.Count)];
            string Shape()
            {
                return random.Next(4) switch
                {
                    0 => Pick(),
                    1 => And(Pick(), Pick()),
                    2 => Or(Pick(), Pick()),
                    _ => And(Pick(), Or(Pick(), Pick())),
                };
            }

            var policy = CompilePolicy(Rule(1, 50, Shape()), Rule(2, 50, Shape()));
            var analysis = RuleOverlapAnalyzer.Analyze(policy);
            var flagged = analysis.Overlaps.Count == 1;
            disjointPairs += flagged ? 0 : 1;
            foreach (var grade in Enumerable.Range(0, 11))
            {
                var evidence = Evaluate(policy, Value(JobGrade, grade.ToString(CultureInfo.InvariantCulture))).Evidence;
                if (evidence.Rules.Count(r => r.Truth == RuleTruth.True) == 2)
                {
                    ambiguousPairs++;
                    flagged.ShouldBeTrue($"round {round}, grade {grade}");
                }
            }
        }

        // The generator must really exercise both sides, or this proves nothing.
        ambiguousPairs.ShouldBeGreaterThan(100);
        disjointPairs.ShouldBeGreaterThan(20);
    }

#pragma warning restore CA5394

    [Fact]
    public void WhateverTheCheckCallsNeverTrueIsNeverTrue()
    {
        var conditions = new List<string>();
        foreach (var value in new[] { "2", "5", "8" })
        {
            conditions.AddRange([
                Cond(JobGrade, "Equals", value), Cond(JobGrade, "NotEquals", value), Cond(JobGrade, "GreaterThan", value), Cond(JobGrade, "LessThanOrEqual", value), Cond(JobGrade, "LessThan", value),
            ]);
        }

        var reported = 0;
        foreach (var first in conditions)
        {
            foreach (var second in conditions)
            {
                var policy = CompilePolicy(Rule(1, 1, And(first, second)));
                var anyTrue = Enumerable.Range(0, 11).Any(g => Evaluate(policy, Value(JobGrade, g.ToString(CultureInfo.InvariantCulture))).Outcome == RuleOutcome.Matched);
                if (RuleOverlapAnalyzer.Analyze(policy).NeverTrueRuleIds.Count > 0)
                {
                    reported++;
                    anyTrue.ShouldBeFalse();
                }
            }
        }

        reported.ShouldBeGreaterThan(10);
    }
}
