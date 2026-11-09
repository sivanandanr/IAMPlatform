using IAM.Modules.Identity.Contracts;
using IAM.Modules.Identity.Domain.Attributes;
using IAM.Modules.Policy.Application.Rules;
using IAM.Modules.Policy.Domain.Rules;
using Shouldly;
using static IAM.Policy.Tests.Fixtures;

namespace IAM.Policy.Tests;

/// <summary>Validation against the catalog (IAM-WF-015): nothing type-invalid or unknown can become a runnable rule.</summary>
public sealed class PolicyCompilerTests
{
    private static RuleRejection? Rejection(params RuleDefinition[] rules) =>
        Should.Throw<RuleRejectedException>(() => CompilePolicy(rules)).Rejection;

    [Theory]
    [InlineData("GreaterThan")]
    [InlineData("GreaterThanOrEqual")]
    [InlineData("LessThan")]
    [InlineData("LessThanOrEqual")]
    public void CodesHaveNoOrdering(string op) =>
        Rejection(Rule(1, 1, Cond(Subgroup, op, "1"))).ShouldBe(RuleRejection.OperatorNotAllowed);

    [Fact]
    public void BooleansHaveNoListsOrOrdering()
    {
        Rejection(Rule(1, 1, Cond(Active, "In", "true"))).ShouldBe(RuleRejection.OperatorNotAllowed);
        Rejection(Rule(1, 1, Cond(Active, "GreaterThan", "true"))).ShouldBe(RuleRejection.OperatorNotAllowed);
    }

    [Fact]
    public void TheCatalogEntryCanNarrowTheOperatorsFurther()
    {
        var narrow = Company with { Operators = new HashSet<RuleOperator> { RuleOperator.Equals } };
        var catalog = new Dictionary<Guid, AttributeSpec> { [narrow.AttributeId] = narrow };
        var version = new PolicyVersionDefinition(Id(500), [Rule(1, 1, Cond(narrow, "In", "A"))]);
        Should.Throw<RuleRejectedException>(() => PolicyCompiler.Compile(version, catalog)).Rejection.ShouldBe(RuleRejection.OperatorNotAllowed);
    }

    [Fact]
    public void ACatalogEntryCannotGrantAnOperatorItsTypeDoesNotAllow()
    {
        // The engine's own table has the last word: bad catalog metadata cannot order codes as text.
        var wrong = Subgroup with { Operators = new HashSet<RuleOperator> { RuleOperator.GreaterThan } };
        var catalog = new Dictionary<Guid, AttributeSpec> { [wrong.AttributeId] = wrong };
        var version = new PolicyVersionDefinition(Id(500), [Rule(1, 1, Cond(wrong, "GreaterThan", "1"))]);
        Should.Throw<RuleRejectedException>(() => PolicyCompiler.Compile(version, catalog)).Rejection.ShouldBe(RuleRejection.OperatorNotAllowed);
    }

    [Fact]
    public void AnUnknownOrUnpublishedAttributeIsRefused()
    {
        var notInCatalog = Spec(99, RuleDataType.Code, maxLength: 5);
        Rejection(Rule(1, 1, Cond(notInCatalog, "Equals", "A"))).ShouldBe(RuleRejection.UnknownAttribute);
        Rejection(Rule(1, 1, Any(notInCatalog)), Rule(2, 2, Cond(Company, "Equals", "A"))).ShouldBe(RuleRejection.WildcardNotAllowed);
    }

    [Theory]
    [InlineData("seven")]
    [InlineData("7.5")]
    [InlineData("1e3")]
    [InlineData("NaN")]
    [InlineData(" 7")]
    [InlineData("+7")]
    [InlineData("0x10")]
    [InlineData("1,000")]
    [InlineData("9223372036854775808")]
    public void IntegerLiteralsAreStrict(string literal) =>
        Rejection(Rule(1, 1, Cond(JobGrade, "Equals", literal))).ShouldBe(RuleRejection.InvalidLiteral);

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("2026-13-01")]
    [InlineData("01/02/2026")]
    [InlineData("2026-1-1")]
    [InlineData("today")]
    public void DateLiteralsAreStrict(string literal) =>
        Rejection(Rule(1, 1, Cond(HireDate, "Equals", literal))).ShouldBe(RuleRejection.InvalidLiteral);

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("^.*$")]
    [InlineData("<script>")]
    [InlineData("a b")]
    [InlineData("")]
    public void CodeLiteralsArePlainCodesAndInjectionShapesAreRefused(string literal) =>
        Rejection(Rule(1, 1, Cond(Company, "Equals", literal))).ShouldBe(RuleRejection.InvalidLiteral);

    [Fact]
    public void ALiteralLongerThanTheAttributeAllowsIsRefused() =>
        Rejection(Rule(1, 1, Cond(Subgroup, "Equals", new string('1', 11)))).ShouldBe(RuleRejection.InvalidLiteral);

    [Theory]
    [InlineData("Equals", 0)]
    [InlineData("Equals", 2)]
    [InlineData("In", 0)]
    public void ValueCountsAreChecked(string op, int count) =>
        Rejection(Rule(1, 1, Cond(Company, op, [.. Enumerable.Range(0, count).Select(i => $"V{i}")]))).ShouldBe(RuleRejection.WrongValueCount);

    [Fact]
    public void NullChecksTakeNoValues()
    {
        var node = $$"""{"type":"condition","subject":"Beneficiary","attributeId":"{{Company.AttributeId:D}}","operator":"IsEmpty","values":["A"]}""";
        Rejection(Rule(1, 1, node)).ShouldBe(RuleRejection.WrongValueCount);
    }

    [Fact]
    public void RepeatedListValuesAreRefusedAfterNormalising()
    {
        Rejection(Rule(1, 1, Cond(Subgroup, "In", "1", "1"))).ShouldBe(RuleRejection.DuplicateLiteral);
        Rejection(Rule(1, 1, Cond(Upper, "In", "abc", " ABC "))).ShouldBe(RuleRejection.DuplicateLiteral);
    }

    [Fact]
    public void WildcardsAreRefusedUnlessTheRuleAllowsThem()
    {
        Rejection(Rule(1, 1, Any(Company))).ShouldBe(RuleRejection.WildcardNotAllowed);
        Should.NotThrow(() => CompilePolicy(Rule(1, 1, Any(Company), wildcard: true)));
    }

    [Fact]
    public void AConditionCanReadAPersonsAttributeForEitherPersonButNotCrossScope()
    {
        Should.NotThrow(() => CompilePolicy(Rule(1, 1, Cond(RuleSubject.Requester, Company, "Equals", "A"))));
        Rejection(Rule(1, 1, Cond(RuleSubject.Application, Company, "Equals", "A"))).ShouldBe(RuleRejection.SubjectNotAllowed);
        Rejection(Rule(1, 1, Cond(RuleSubject.Beneficiary, Entitlement, "Equals", "A"))).ShouldBe(RuleRejection.SubjectNotAllowed);
    }

    [Fact]
    public void RuleCountsAreBoundedExactlyAtTheLimit()
    {
        var rules = Enumerable.Range(1, RuleLimits.MaxRulesPerVersion).Select(i => Rule(i, i, Cond(Company, "Equals", "A"))).ToArray();
        Should.NotThrow(() => CompilePolicy(rules));

        var tooMany = Enumerable.Range(1, RuleLimits.MaxRulesPerVersion + 1).Select(i => Rule(i, i, Cond(Company, "Equals", "A"))).ToArray();
        Rejection(tooMany).ShouldBe(RuleRejection.TooManyRules);
    }

    [Fact]
    public void AVersionMustHaveRulesWithUniqueIdsAndValidSettings()
    {
        var valid = Cond(Company, "Equals", "A");
        Rejection().ShouldBe(RuleRejection.EmptyPolicy);
        Rejection(Rule(1, 1, valid), Rule(1, 2, valid)).ShouldBe(RuleRejection.DuplicateRuleId);
        Rejection(new RuleDefinition(Guid.Empty, 1, null, null, false, Doc(valid))).ShouldBe(RuleRejection.DuplicateRuleId);
        Rejection(Rule(1, -1, valid)).ShouldBe(RuleRejection.InvalidPriority);
        Rejection(Rule(1, RuleLimits.MaxPriority + 1, valid)).ShouldBe(RuleRejection.InvalidPriority);
        Rejection(Rule(1, 1, valid, from: Now, to: Now)).ShouldBe(RuleRejection.InvalidEffectivePeriod);
        Rejection(Rule(1, 1, valid, from: Now, to: Now.AddDays(-1))).ShouldBe(RuleRejection.InvalidEffectivePeriod);
    }

    [Fact]
    public void RulesAreOrderedByPriorityThenIdWhateverTheAuthoredOrder()
    {
        var valid = Cond(Company, "Equals", "A");
        var policy = CompilePolicy(Rule(3, 10, valid), Rule(1, 10, valid), Rule(2, 50, valid));
        policy.Rules.Select(r => r.RuleId).ShouldBe([Id(2), Id(1), Id(3)]);
    }

    [Fact]
    public void TheHashIgnoresAuthoringOrderAndListOrderOnly()
    {
        var first = CompilePolicy(Rule(1, 10, Cond(Subgroup, "In", "1", "2")), Rule(2, 20, Cond(Company, "Equals", "A")));
        var reordered = CompilePolicy(Rule(2, 20, Cond(Company, "Equals", "A")), Rule(1, 10, Cond(Subgroup, "In", "2", "1")));
        reordered.ContentHash.ShouldBe(first.ContentHash);
        first.ContentHash.Length.ShouldBe(64);
    }

    [Fact]
    public void TheHashChangesWithAnythingThatChangesADecision()
    {
        var baseline = CompilePolicy(Rule(1, 10, Cond(Subgroup, "In", "1", "2"))).ContentHash;
        CompilePolicy(Rule(1, 11, Cond(Subgroup, "In", "1", "2"))).ContentHash.ShouldNotBe(baseline);
        CompilePolicy(Rule(2, 10, Cond(Subgroup, "In", "1", "2"))).ContentHash.ShouldNotBe(baseline);
        CompilePolicy(Rule(1, 10, Cond(Subgroup, "In", "1", "3"))).ContentHash.ShouldNotBe(baseline);
        CompilePolicy(Rule(1, 10, Cond(Subgroup, "NotIn", "1", "2"))).ContentHash.ShouldNotBe(baseline);
        CompilePolicy(Rule(1, 10, Cond(Subgroup, "In", "01", "2"))).ContentHash.ShouldNotBe(baseline);
        CompilePolicy(Rule(1, 10, Cond(Subgroup, "In", "1", "2"), from: Now)).ContentHash.ShouldNotBe(baseline);
        CompilePolicy(Rule(1, 10, Cond(RuleSubject.Requester, Subgroup, "In", "1", "2"))).ContentHash.ShouldNotBe(baseline);
    }

    [Fact]
    public void TheHashChangesWhenTheAttributeDefinitionVersionChanges()
    {
        var rule = Rule(1, 10, Cond(Subgroup, "In", "1"));
        var baseline = CompilePolicy(rule).ContentHash;
        var catalog = new Dictionary<Guid, AttributeSpec> { [Subgroup.AttributeId] = Subgroup with { VersionId = Id(7777), ContentHash = "changed" } };
        PolicyCompiler.Compile(new PolicyVersionDefinition(Id(500), [rule]), catalog).ContentHash.ShouldNotBe(baseline);
    }

    [Fact]
    public void AttributeMetadataFromTheCatalogIsMappedStrictly()
    {
        var published = new PublishedAttributeDefinition(
            Id(1), Id(2), 1, "ee_subgroup_code", "EE subgroup", "Code", "Beneficiary", "successfactors", "Internal", "Trim", 60, 10, ["Equals", "In"], new string('a', 64));
        var spec = AttributeSpecMapper.Map(published);
        spec.DataType.ShouldBe(RuleDataType.Code);
        spec.Normalization.ShouldBe(RuleNormalization.Trim);
        spec.Operators.ShouldBe(new[] { RuleOperator.Equals, RuleOperator.In }, ignoreOrder: true);

        foreach (var bad in new[]
        {
            published with { DataType = "code" },
            published with { DataType = "3" },
            published with { SubjectScope = "Anyone" },
            published with { Sensitivity = "Low" },
            published with { Normalization = "StripZeros" },
            published with { Operators = ["Equals", "Regex"] },
            published with { FreshnessMaxAgeMinutes = 0 },
            published with { FreshnessMaxAgeMinutes = 525_601 },
            published with { Operators = null! },
            published with { Operators = [] },
            published with { Operators = ["Equals", "Equals"] },
            published with { ContentHash = "hash" },
            published with { ContentHash = null! },
            published with { ContentHash = new string('A', 64) },
            published with { AttributeDefinitionId = Guid.Empty },
            published with { VersionId = Guid.Empty },
            published with { MaxLength = null },
            published with { MaxLength = 401 },
            published with { DataType = "Integer", MaxLength = null },
            published with { DataType = "Integer", Normalization = "None" },
        })
        {
            Should.Throw<RuleRejectedException>(() => AttributeSpecMapper.Map(bad)).Rejection.ShouldBe(RuleRejection.InvalidAttributeMetadata);
        }
    }

    [Fact]
    public void EnumNamesMatchTheIdentityCatalogSoMappingCannotDrift()
    {
        Enum.GetNames<RuleDataType>().ShouldBe(Enum.GetNames<AttributeDataType>(), ignoreOrder: true);
        Enum.GetNames<RuleOperator>().ShouldBe(Enum.GetNames<AttributeOperator>(), ignoreOrder: true);
        Enum.GetNames<RuleSubject>().ShouldBe(Enum.GetNames<AttributeSubjectScope>(), ignoreOrder: true);
        Enum.GetNames<RuleSensitivity>().ShouldBe(Enum.GetNames<AttributeSensitivity>(), ignoreOrder: true);
        Enum.GetNames<RuleNormalization>().ShouldBe(Enum.GetNames<AttributeNormalization>(), ignoreOrder: true);
        foreach (var type in Enum.GetValues<AttributeDataType>())
        {
            var engine = RuleVocabulary.AllowedOperators(Enum.Parse<RuleDataType>(type.ToString())).Select(o => o.ToString());
            engine.ShouldBe(AttributeRules.AllowedOperators(type).Select(o => o.ToString()), ignoreOrder: true);
        }
    }
}
