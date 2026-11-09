using IAM.Modules.Policy.Domain.Rules;
using Shouldly;
using static IAM.Policy.Tests.Fixtures;

namespace IAM.Policy.Tests;

/// <summary>Pre-publication overlap check (IAM-WF-005, IAM-WF-016, FR-POL-102). It may flag a harmless pair, never miss a real one.</summary>
public sealed class RuleOverlapAnalyzerTests
{
    private static IReadOnlyList<RuleOverlap> Find(params RuleDefinition[] rules) => RuleOverlapAnalyzer.Analyze(CompilePolicy(rules)).Overlaps;

    [Fact]
    public void IdenticalRulesAtTheSamePriorityOverlap()
    {
        var overlap = Find(Rule(1, 50, Cond(Company, "Equals", "A")), Rule(2, 50, Cond(Company, "Equals", "A"))).ShouldHaveSingleItem();
        overlap.Priority.ShouldBe(50);
        new[] { overlap.FirstRuleId, overlap.SecondRuleId }.ShouldBe([Id(1), Id(2)], ignoreOrder: true);
    }

    [Fact]
    public void RulesAtDifferentPrioritiesDoNotOverlap() =>
        Find(Rule(1, 50, Cond(Company, "Equals", "A")), Rule(2, 51, Cond(Company, "Equals", "A"))).ShouldBeEmpty();

    [Fact]
    public void DifferentValuesOfOneAttributeAreDisjoint() =>
        Find(Rule(1, 50, Cond(Company, "Equals", "A")), Rule(2, 50, Cond(Company, "Equals", "B"))).ShouldBeEmpty();

    [Fact]
    public void ListsWithNoSharedValueAreDisjointAndASharedValueOverlaps()
    {
        Find(Rule(1, 50, Cond(Subgroup, "In", "1", "2")), Rule(2, 50, Cond(Subgroup, "In", "3"))).ShouldBeEmpty();
        Find(Rule(1, 50, Cond(Subgroup, "In", "1", "2")), Rule(2, 50, Cond(Subgroup, "In", "2", "3"))).ShouldHaveSingleItem();
    }

    [Fact]
    public void CodesDifferingOnlyByALeadingZeroAreDifferentValues() =>
        Find(Rule(1, 50, Cond(Subgroup, "In", "1")), Rule(2, 50, Cond(Subgroup, "In", "01"))).ShouldBeEmpty();

    [Fact]
    public void DifferentAttributesCanBothBeTrueSoTheyOverlap() =>
        Find(Rule(1, 50, Cond(Company, "Equals", "A")), Rule(2, 50, Cond(Department, "Equals", "B"))).ShouldHaveSingleItem();

    [Fact]
    public void DisjointInsideNestedAndGroupsIsRecognised() =>
        Find(
            Rule(1, 50, And(Cond(Department, "Equals", "X"), And(Cond(Company, "Equals", "A")))),
            Rule(2, 50, And(Cond(Department, "Equals", "X"), And(Cond(Company, "Equals", "B"))))).ShouldBeEmpty();

    [Fact]
    public void RequiringAnAttributeTwiceNarrowsItToTheSharedValues() =>
        Find(
            Rule(1, 50, And(Cond(Subgroup, "In", "1", "2"), Cond(Subgroup, "In", "2", "3"))),
            Rule(2, 50, Cond(Subgroup, "In", "1", "3"))).ShouldBeEmpty();

    [Fact]
    public void AnOrGroupIsNotAssumedDisjoint() =>
        Find(
            Rule(1, 50, Or(Cond(Company, "Equals", "A"), Cond(Department, "Equals", "X"))),
            Rule(2, 50, Cond(Company, "Equals", "B"))).ShouldHaveSingleItem();

    [Fact]
    public void TheSameValueReadForDifferentPeopleIsNotDisjoint() =>
        Find(
            Rule(1, 50, Cond(RuleSubject.Beneficiary, Company, "Equals", "A")),
            Rule(2, 50, Cond(RuleSubject.Requester, Company, "Equals", "B"))).ShouldHaveSingleItem();

    [Fact]
    public void EffectivePeriodsThatDoNotMeetAreDisjointAndTouchingEndsDoNotMeet()
    {
        var node = Cond(Company, "Equals", "A");
        Find(Rule(1, 50, node, to: Now), Rule(2, 50, node, from: Now)).ShouldBeEmpty();
        Find(Rule(1, 50, node, from: Now), Rule(2, 50, node, to: Now)).ShouldBeEmpty();
        Find(Rule(1, 50, node, to: Now.AddTicks(1)), Rule(2, 50, node, from: Now)).ShouldHaveSingleItem();
        Find(Rule(1, 50, node, from: Now), Rule(2, 50, node)).ShouldHaveSingleItem();
    }

    [Fact]
    public void TheL3MatrixHasNoEqualPriorityOverlap()
    {
        var finance = And(Cond(Company, "Equals", "ABC"), Cond(Department, "Equals", "Finance"));
        Find(
            Rule(100, 100, And(finance, Cond(Subgroup, "In", "1", "2", "3", "4", "5", "6"))),
            Rule(110, 110, And(finance, Cond(CostCenter, "Equals", "CC1001"), Cond(JobGrade, "GreaterThanOrEqual", "7"))),
            Rule(900, 10, finance)).ShouldBeEmpty();
    }
}
