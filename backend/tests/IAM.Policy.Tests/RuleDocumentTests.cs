using IAM.Modules.Policy.Domain.Rules;
using Shouldly;
using static IAM.Policy.Tests.Fixtures;

namespace IAM.Policy.Tests;

/// <summary>Hostile and malformed rule documents (BACKEND_INPUT_VALIDATION, ADR-0004). Everything is refused before evaluation.</summary>
public sealed class RuleDocumentTests
{
    private static RuleRejection? Rejection(string json) =>
        Should.Throw<RuleRejectedException>(() => RuleDocumentParser.Parse(json)).Rejection;

    private static string Valid => Cond(Company, "Equals", "ABC");

    [Fact]
    public void AValidNestedDocumentParses()
    {
        var node = RuleDocumentParser.Parse(Doc(And(Valid, Or(Cond(JobGrade, "GreaterThanOrEqual", "7"), Any(Company)))));
        var group = node.ShouldBeOfType<RuleGroupNode>();
        group.Children.Count.ShouldBe(2);
        group.Children[1].ShouldBeOfType<RuleGroupNode>().Children[1].ShouldBeOfType<RuleWildcardNode>();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":1,\"root\":{\"type\":\"wildcard\",}}")]
    public void MalformedJsonIsRefused(string json) =>
        Should.Throw<RuleRejectedException>(() => RuleDocumentParser.Parse(json));

    [Fact]
    public void CommentsAreRefused() =>
        Rejection("/* x */ " + Doc(Valid)).ShouldBe(RuleRejection.InvalidJson);

    [Fact]
    public void TrailingContentIsRefused() =>
        Rejection(Doc(Valid) + " {}").ShouldBe(RuleRejection.InvalidJson);

    [Fact]
    public void ANullDocumentIsRefused() =>
        Should.Throw<RuleRejectedException>(() => RuleDocumentParser.Parse(null)).Rejection.ShouldBe(RuleRejection.TooLarge);

    [Fact]
    public void LoneSurrogatesAreRefused() =>
        Rejection(Doc(Valid) + (char)0xD800).ShouldBe(RuleRejection.InvalidJson);

    [Fact]
    public void ADocumentOverTheSizeLimitIsRefusedByLengthAndByBytes()
    {
        Rejection(new string(' ', RuleLimits.MaxRuleBytes + 1)).ShouldBe(RuleRejection.TooLarge);

        // Short in characters, over the limit in UTF-8: three bytes per character.
        var wide = new string((char)0x20AC, (RuleLimits.MaxRuleBytes / 3) + 1);
        Rejection(wide).ShouldBe(RuleRejection.TooLarge);
    }

    [Fact]
    public void ADocumentExactlyAtTheSizeLimitIsRead()
    {
        var doc = Doc(Valid);
        var padded = doc + new string(' ', RuleLimits.MaxRuleBytes - doc.Length);
        padded.Length.ShouldBe(RuleLimits.MaxRuleBytes);
        Should.NotThrow(() => RuleDocumentParser.Parse(padded));
    }

    [Theory]
    [InlineData("status", "\"Published\"")]
    [InlineData("priority", "1000")]
    [InlineData("isAdmin", "true")]
    [InlineData("Root", "{}")]
    public void UnknownDocumentPropertiesAreRefused(string name, string value) =>
        Rejection($$"""{"schemaVersion":1,"{{name}}":{{value}},"root":{{Valid}}}""").ShouldBe(RuleRejection.UnknownProperty);

    [Fact]
    public void ADisplayKeyCannotStandInForOrSitBesideTheAttributeId()
    {
        // attributeId is the only reference: a label could make a rule look like it tests something else.
        var spoofed = $$"""{"type":"condition","subject":"Beneficiary","attributeId":"{{Company.AttributeId:D}}","attributeKey":"department","operator":"Equals","values":["ABC"]}""";
        Rejection(Doc(spoofed)).ShouldBe(RuleRejection.UnknownProperty);
    }

    [Fact]
    public void DuplicatePropertiesAreRefusedEvenWhenWrittenWithEscapes()
    {
        Rejection($$"""{"schemaVersion":1,"schemaVersion":1,"root":{{Valid}}}""").ShouldBe(RuleRejection.DuplicateProperty);

        var duplicateOperator = $$"""{"type":"condition","subject":"Beneficiary","attributeId":"{{Company.AttributeId:D}}","operator":"Equals","operator":"NotEquals","values":["ABC"]}""";
        Rejection(Doc(duplicateOperator)).ShouldBe(RuleRejection.DuplicateProperty);

        var duplicateType = $$"""{"type":"group","type":"wildcard","subject":"Beneficiary","attributeId":"{{Company.AttributeId:D}}"}""";
        Rejection(Doc(duplicateType)).ShouldBe(RuleRejection.DuplicateProperty);
    }

    [Theory]
    [InlineData("2", RuleRejection.UnsupportedSchemaVersion)]
    [InlineData("0", RuleRejection.UnsupportedSchemaVersion)]
    [InlineData("\"1\"", RuleRejection.InvalidPropertyType)]
    [InlineData("1.5", RuleRejection.InvalidPropertyType)]
    public void OnlyTheKnownSchemaVersionIsRead(string version, RuleRejection expected) =>
        Rejection($$"""{"schemaVersion":{{version}},"root":{{Valid}}}""").ShouldBe(expected);

    [Fact]
    public void AMissingSchemaVersionOrRootIsRefused()
    {
        Rejection($$"""{"root":{{Valid}}}""").ShouldBe(RuleRejection.MissingProperty);
        Rejection("""{"schemaVersion":1}""").ShouldBe(RuleRejection.MissingProperty);
    }

    [Theory]
    [InlineData("""{"type":"expression","code":"1==1"}""", RuleRejection.UnknownNodeType)]
    [InlineData("""{"type":"sql","query":"select 1"}""", RuleRejection.UnknownNodeType)]
    [InlineData("""{"operator":"And","children":[]}""", RuleRejection.MissingProperty)]
    [InlineData("""{"type":1}""", RuleRejection.InvalidPropertyType)]
    [InlineData("""{"type":"group","operator":"And","children":[]}""", RuleRejection.EmptyGroup)]
    [InlineData("""{"type":"group","operator":"XOR","children":[{"type":"group"}]}""", RuleRejection.UnknownGroupOperator)]
    [InlineData("""{"type":"group","operator":"and","children":[{"type":"group"}]}""", RuleRejection.UnknownGroupOperator)]
    [InlineData("""{"type":"group","operator":"And","children":{}}""", RuleRejection.InvalidPropertyType)]
    [InlineData("\"Company\"", RuleRejection.InvalidPropertyType)]
    public void BadNodesAreRefused(string node, RuleRejection expected) =>
        Rejection(Doc(node)).ShouldBe(expected);

    [Theory]
    [InlineData("Regex")]
    [InlineData("Contains")]
    [InlineData("StartsWith")]
    [InlineData("equals")]
    [InlineData("1")]
    public void OperatorsOutsideTheAllowlistAreRefused(string op) =>
        Rejection(Doc(Cond(Company, op, "ABC"))).ShouldBe(RuleRejection.UnknownOperator);

    [Theory]
    [InlineData("Everyone")]
    [InlineData("beneficiary")]
    [InlineData("1")]
    public void SubjectsOutsideTheAllowlistAreRefused(string subject)
    {
        var node = $$"""{"type":"condition","subject":"{{subject}}","attributeId":"{{Company.AttributeId:D}}","operator":"Equals","values":["ABC"]}""";
        Rejection(Doc(node)).ShouldBe(RuleRejection.UnknownSubject);
    }

    [Fact]
    public void AConditionMustNameItsSubjectAndThereIsNoDefault()
    {
        var node = $$"""{"type":"condition","attributeId":"{{Company.AttributeId:D}}","operator":"Equals","values":["ABC"]}""";
        Rejection(Doc(node)).ShouldBe(RuleRejection.MissingProperty);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("company")]
    [InlineData("{00000000-0000-0000-0000-000000000001}")]
    [InlineData("00000000000000000000000000000001")]
    public void AttributeIdsMustBeRealGuidsInOneFormat(string id)
    {
        var node = $$"""{"type":"condition","subject":"Beneficiary","attributeId":"{{id}}","operator":"Equals","values":["ABC"]}""";
        Rejection(Doc(node)).ShouldBe(RuleRejection.InvalidAttributeId);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("[true]")]
    [InlineData("[null]")]
    [InlineData("[[\"a\"]]")]
    [InlineData("[{\"a\":1}]")]
    [InlineData("\"ABC\"")]
    public void LiteralsMustBeJsonStrings(string values)
    {
        // A numeric literal for a Code attribute is the case ADR-0004 names: 1 must not silently become "1".
        var node = $$"""{"type":"condition","subject":"Beneficiary","attributeId":"{{Company.AttributeId:D}}","operator":"In","values":{{values}}}""";
        Rejection(Doc(node)).ShouldBe(RuleRejection.InvalidPropertyType);
    }

    [Fact]
    public void GroupDepthIsBoundedExactlyAtTheLimit()
    {
        var atLimit = Doc(And(And(And(And(Valid)))));
        Should.NotThrow(() => RuleDocumentParser.Parse(atLimit));

        var overLimit = Doc(And(And(And(And(And(Valid))))));
        Rejection(overLimit).ShouldBe(RuleRejection.TooDeep);
    }

    [Fact]
    public void ExtremelyDeepNestingIsRefusedWithoutOverflowingTheStack()
    {
        var deep = new string('[', 5000);
        Rejection(deep).ShouldBe(RuleRejection.InvalidJson);

        var nested = Valid;
        for (var i = 0; i < 40; i++)
        {
            nested = And(nested);
        }

        Rejection(Doc(nested)).ShouldBe(RuleRejection.InvalidJson);
    }

    [Fact]
    public void ConditionsPerRuleAreBoundedExactlyAtTheLimit()
    {
        var atLimit = Enumerable.Repeat(Cond(Company, "Equals", "A"), RuleLimits.MaxConditionsPerRule).ToArray();
        Should.NotThrow(() => RuleDocumentParser.Parse(Doc(And(atLimit))));

        var overLimit = Enumerable.Repeat(Cond(Company, "Equals", "A"), RuleLimits.MaxConditionsPerRule + 1).ToArray();
        Rejection(Doc(And(overLimit))).ShouldBe(RuleRejection.TooManyConditions);
    }

    [Fact]
    public void ListSizeIsBoundedExactlyAtTheLimit()
    {
        var atLimit = Enumerable.Range(0, RuleLimits.MaxListValues).Select(i => $"V{i}").ToArray();
        Should.NotThrow(() => RuleDocumentParser.Parse(Doc(Cond(Company, "In", atLimit))));

        var overLimit = Enumerable.Range(0, RuleLimits.MaxListValues + 1).Select(i => $"V{i}").ToArray();
        Rejection(Doc(Cond(Company, "In", overLimit))).ShouldBe(RuleRejection.TooManyValues);
    }

    [Fact]
    public void LiteralLengthIsBoundedExactlyAtTheLimit()
    {
        Should.NotThrow(() => RuleDocumentParser.Parse(Doc(Cond(Company, "Equals", new string('A', RuleLimits.MaxLiteralLength)))));
        Rejection(Doc(Cond(Company, "Equals", new string('A', RuleLimits.MaxLiteralLength + 1)))).ShouldBe(RuleRejection.LiteralTooLong);
    }

    [Fact]
    public void RejectionsDoNotEchoTheDocument()
    {
        const string Secret = "s3cret-value-from-the-document";
        var messages = new[]
        {
            Doc(Cond(Company, Secret, "x")),
            $$"""{"schemaVersion":1,"{{Secret}}":1,"root":{{Valid}}}""",
            Secret,
        }.Select(json => Should.Throw<RuleRejectedException>(() => RuleDocumentParser.Parse(json)).Message);

        foreach (var message in messages)
        {
            message.ShouldNotContain(Secret);
        }
    }
}
