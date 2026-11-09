using IAM.Modules.Policy.Domain.Rules;
using Shouldly;
using static IAM.Policy.Tests.Fixtures;

namespace IAM.Policy.Tests;

/// <summary>Evaluation and rule selection (IAM-WF-005, -015, -016, -018, -020, -022; P0-008; spec L.3, L.7). No path may turn doubt into a match.</summary>
public sealed class PolicyEvaluatorTests
{
    private static readonly SnapshotEntry[] FinancePerson =
    [
        Value(Company, "ABC"), Value(Department, "Finance"), Value(Subgroup, "3"), Value(CostCenter, "CC2000"), Value(JobGrade, "5"),
    ];

    /// <summary>Spec L.3: R-100, R-110 and R-900 as written, with EESubgroupCode as strings (DL-006, DL-038).</summary>
    private static CompiledPolicy L3Matrix() => CompilePolicy(
        Rule(100, 100, And(Cond(Company, "Equals", "ABC"), Cond(Department, "Equals", "Finance"), Cond(Subgroup, "In", "1", "2", "3", "4", "5", "6"))),
        Rule(110, 110, And(Cond(Company, "Equals", "ABC"), Cond(Department, "Equals", "Finance"), Cond(CostCenter, "Equals", "CC1001"), Cond(JobGrade, "GreaterThanOrEqual", "7"))),
        Rule(900, 10, And(Cond(Company, "Equals", "ABC"), Cond(Department, "Equals", "Finance"))));

    private static RuleTruth TruthOf(string node, params SnapshotEntry[] entries) =>
        Evaluate(CompilePolicy(Rule(1, 1, node, wildcard: true)), entries).Evidence.Rules[0].Truth;

    // A true, false and unknown leaf: Company is present, Department is never synchronised.
    private static readonly string T = Cond(Company, "Equals", "ABC");
    private static readonly string F = Cond(Company, "Equals", "XYZ");
    private static readonly string U = Cond(Department, "Equals", "Finance");

    public static TheoryData<string, string, string, RuleTruth> Kleene() => new()
    {
        { "And", T, T, RuleTruth.True },
        { "And", T, F, RuleTruth.False },
        { "And", T, U, RuleTruth.Unknown },
        { "And", F, T, RuleTruth.False },
        { "And", F, F, RuleTruth.False },
        { "And", F, U, RuleTruth.False },
        { "And", U, T, RuleTruth.Unknown },
        { "And", U, F, RuleTruth.False },
        { "And", U, U, RuleTruth.Unknown },
        { "Or", T, T, RuleTruth.True },
        { "Or", T, F, RuleTruth.True },
        { "Or", T, U, RuleTruth.True },
        { "Or", F, T, RuleTruth.True },
        { "Or", F, F, RuleTruth.False },
        { "Or", F, U, RuleTruth.Unknown },
        { "Or", U, T, RuleTruth.True },
        { "Or", U, F, RuleTruth.Unknown },
        { "Or", U, U, RuleTruth.Unknown },
    };

    [Theory]
    [MemberData(nameof(Kleene))]
    public void ThreeValuedLogicTruthTable(string op, string left, string right, RuleTruth expected) =>
        TruthOf(Group(op, left, right), Value(Company, "ABC")).ShouldBe(expected);

    public static TheoryData<string, string[], string?, RuleTruth> Comparisons() => new()
    {
        { "Equals", ["5"], "5", RuleTruth.True },
        { "Equals", ["5"], "6", RuleTruth.False },
        { "NotEquals", ["5"], "6", RuleTruth.True },
        { "NotEquals", ["5"], "5", RuleTruth.False },
        { "In", ["1", "5"], "5", RuleTruth.True },
        { "In", ["1", "5"], "6", RuleTruth.False },
        { "NotIn", ["1", "5"], "6", RuleTruth.True },
        { "NotIn", ["1", "5"], "5", RuleTruth.False },
        { "GreaterThan", ["7"], "8", RuleTruth.True },
        { "GreaterThan", ["7"], "7", RuleTruth.False },
        { "GreaterThanOrEqual", ["7"], "7", RuleTruth.True },
        { "GreaterThanOrEqual", ["7"], "6", RuleTruth.False },
        { "LessThan", ["7"], "6", RuleTruth.True },
        { "LessThan", ["7"], "7", RuleTruth.False },
        { "LessThanOrEqual", ["7"], "7", RuleTruth.True },
        { "LessThanOrEqual", ["7"], "8", RuleTruth.False },
        { "IsEmpty", [], "7", RuleTruth.False },
        { "IsNotEmpty", [], "7", RuleTruth.True },
        { "IsEmpty", [], null, RuleTruth.True },
        { "IsNotEmpty", [], null, RuleTruth.False },

        // A value the source confirms is absent still cannot pass a comparison.
        { "Equals", ["5"], null, RuleTruth.Unknown },
        { "NotEquals", ["5"], null, RuleTruth.Unknown },
        { "In", ["5"], null, RuleTruth.Unknown },
        { "NotIn", ["5"], null, RuleTruth.Unknown },
        { "GreaterThan", ["5"], null, RuleTruth.Unknown },
        { "LessThan", ["5"], null, RuleTruth.Unknown },
    };

    [Theory]
    [MemberData(nameof(Comparisons))]
    public void EveryOperatorComparesByType(string op, string[] literals, string? value, RuleTruth expected) =>
        TruthOf(Cond(JobGrade, op, literals), Value(JobGrade, value)).ShouldBe(expected);

    [Fact]
    public void NumbersCompareAsNumbersNotText() =>
        TruthOf(Cond(JobGrade, "GreaterThan", "7"), Value(JobGrade, "10")).ShouldBe(RuleTruth.True);

    [Fact]
    public void DatesDecimalsAndTimestampsCompareByValue()
    {
        TruthOf(Cond(HireDate, "LessThan", "2026-01-01"), Value(HireDate, "2025-12-31")).ShouldBe(RuleTruth.True);
        TruthOf(Cond(Salary, "Equals", "12.5"), Value(Salary, "12.50")).ShouldBe(RuleTruth.True);
        TruthOf(Cond(Salary, "GreaterThan", "9.5"), Value(Salary, "10.25")).ShouldBe(RuleTruth.True);
        TruthOf(Cond(LastSeen, "GreaterThan", "2026-01-01T00:00:00Z"), Value(LastSeen, "2026-01-01T00:00:00.000001Z")).ShouldBe(RuleTruth.True);
        TruthOf(Cond(Active, "Equals", "true"), Value(Active, "true")).ShouldBe(RuleTruth.True);
        TruthOf(Cond(Active, "Equals", "true"), Value(Active, "false")).ShouldBe(RuleTruth.False);
    }

    [Theory]
    [InlineData("1", "1", RuleTruth.True)]
    [InlineData("01", "01", RuleTruth.True)]
    [InlineData("01", "1", RuleTruth.False)]
    [InlineData("1", "01", RuleTruth.False)]
    [InlineData("001", "01", RuleTruth.False)]
    public void LeadingZerosAreKeptAndCodesCompareExactly(string value, string literal, RuleTruth expected) =>
        TruthOf(Cond(Subgroup, "In", literal), Value(Subgroup, value)).ShouldBe(expected);

    [Fact]
    public void CodesAreCaseSensitiveUnlessTheAttributeDeclaresOtherwise()
    {
        TruthOf(Cond(Company, "Equals", "ABC"), Value(Company, "abc")).ShouldBe(RuleTruth.False);
        TruthOf(Cond(Upper, "Equals", "abc"), Value(Upper, " ABC ")).ShouldBe(RuleTruth.True);
    }

    public static TheoryData<string, string[]> EveryOperator() => new()
    {
        { "Equals", ["5"] }, { "NotEquals", ["5"] }, { "In", ["5"] }, { "NotIn", ["5"] }, { "GreaterThan", ["5"] },
        { "GreaterThanOrEqual", ["5"] }, { "LessThan", ["5"] }, { "LessThanOrEqual", ["5"] }, { "IsEmpty", [] }, { "IsNotEmpty", [] },
    };

    [Theory]
    [MemberData(nameof(EveryOperator))]
    public void ANeverSynchronisedValueIsUnknownForEveryOperator(string op, string[] literals) =>
        TruthOf(Cond(JobGrade, op, literals)).ShouldBe(RuleTruth.Unknown);

    [Theory]
    [MemberData(nameof(EveryOperator))]
    public void AStaleValueIsUnknownForEveryOperatorEvenANullCheck(string op, string[] literals)
    {
        TruthOf(Cond(JobGrade, op, literals), Value(JobGrade, "5", Now.AddMinutes(-61))).ShouldBe(RuleTruth.Unknown);
        TruthOf(Cond(JobGrade, op, literals), Value(JobGrade, null, Now.AddMinutes(-61))).ShouldBe(RuleTruth.Unknown);
    }

    [Theory]
    [MemberData(nameof(EveryOperator))]
    public void AValueDatedInTheFutureIsUnknownForEveryOperator(string op, string[] literals) =>
        TruthOf(Cond(JobGrade, op, literals), Value(JobGrade, "5", Now.AddSeconds(1))).ShouldBe(RuleTruth.Unknown);

    [Theory]
    [MemberData(nameof(EveryOperator))]
    public void AnInvalidValueIsUnknownForEveryOperator(string op, string[] literals) =>
        TruthOf(Cond(JobGrade, op, literals), Value(JobGrade, "not-a-number")).ShouldBe(RuleTruth.Unknown);

    [Theory]
    [MemberData(nameof(EveryOperator))]
    public void ConflictingEntriesAreUnknownForEveryOperator(string op, string[] literals) =>
        TruthOf(Cond(JobGrade, op, literals), Value(JobGrade, "5"), Value(JobGrade, "5")).ShouldBe(RuleTruth.Unknown);

    [Fact]
    public void AnEmptyStringIsNotAValue()
    {
        TruthOf(Cond(Company, "NotEquals", "ABC"), Value(Company, string.Empty)).ShouldBe(RuleTruth.Unknown);
        TruthOf(Cond(Company, "IsEmpty"), Value(Company, string.Empty)).ShouldBe(RuleTruth.Unknown);
    }

    [Fact]
    public void FreshnessIsInclusiveAtTheExactLimit()
    {
        var node = Cond(JobGrade, "Equals", "5");
        TruthOf(node, Value(JobGrade, "5", Now.AddMinutes(-60))).ShouldBe(RuleTruth.True);
        TruthOf(node, Value(JobGrade, "5", Now.AddMinutes(-60).AddTicks(-1))).ShouldBe(RuleTruth.Unknown);
        TruthOf(node, Value(JobGrade, "5", Now)).ShouldBe(RuleTruth.True);
    }

    [Fact]
    public void WildcardIsTrueOnlyForAFreshValidValue()
    {
        TruthOf(Any(Company), Value(Company, "ANY")).ShouldBe(RuleTruth.True);
        TruthOf(Any(Company)).ShouldBe(RuleTruth.Unknown);
        TruthOf(Any(Company), Value(Company, null)).ShouldBe(RuleTruth.Unknown);
        TruthOf(Any(Company), Value(Company, "ANY", Now.AddHours(-2))).ShouldBe(RuleTruth.Unknown);
        TruthOf(Any(Company), Value(Company, "not valid")).ShouldBe(RuleTruth.Unknown);
    }

    [Fact]
    public void BeneficiaryAndRequesterValuesNeverMix()
    {
        var requesterOnly = Value(Company, "ABC", subject: RuleSubject.Requester);
        TruthOf(Cond(RuleSubject.Beneficiary, Company, "Equals", "ABC"), requesterOnly).ShouldBe(RuleTruth.Unknown);
        TruthOf(Cond(RuleSubject.Requester, Company, "Equals", "ABC"), requesterOnly).ShouldBe(RuleTruth.True);
    }

    [Fact]
    public void TheSpecNestedExampleEvaluatesAsDesigned()
    {
        // IAM-WF-015: (Company=ABC AND Department=Finance AND EESubgroupCode IN [1..6]) OR (CostCenter=CC1001 AND JobGrade>=7)
        var node = Or(
            And(Cond(Company, "Equals", "ABC"), Cond(Department, "Equals", "Finance"), Cond(Subgroup, "In", "1", "2", "3", "4", "5", "6")),
            And(Cond(CostCenter, "Equals", "CC1001"), Cond(JobGrade, "GreaterThanOrEqual", "7")));

        SnapshotEntry[] First = [Value(Company, "ABC"), Value(Department, "Finance"), Value(Subgroup, "6"), Value(CostCenter, "X"), Value(JobGrade, "1")];
        SnapshotEntry[] Second = [Value(Company, "XYZ"), Value(Department, "HR"), Value(Subgroup, "9"), Value(CostCenter, "CC1001"), Value(JobGrade, "7")];
        SnapshotEntry[] Neither = [Value(Company, "ABC"), Value(Department, "Finance"), Value(Subgroup, "7"), Value(CostCenter, "CC1001"), Value(JobGrade, "6")];
        SnapshotEntry[] LeadingZero = [Value(Company, "ABC"), Value(Department, "Finance"), Value(Subgroup, "06"), Value(CostCenter, "X"), Value(JobGrade, "1")];

        TruthOf(node, First).ShouldBe(RuleTruth.True);
        TruthOf(node, Second).ShouldBe(RuleTruth.True);
        TruthOf(node, Neither).ShouldBe(RuleTruth.False);
        TruthOf(node, LeadingZero).ShouldBe(RuleTruth.False);

        // The second branch settles it, so the missing first branch cannot change the answer.
        TruthOf(node, Value(CostCenter, "CC1001"), Value(JobGrade, "9")).ShouldBe(RuleTruth.True);
        TruthOf(node, Value(CostCenter, "CC1001"), Value(JobGrade, "1")).ShouldBe(RuleTruth.Unknown);
    }

    [Fact]
    public void L3MatrixSelectsByPriority()
    {
        // R-110 beats R-100 because it has the higher priority, not because it is more specific (DL-011).
        Evaluate(L3Matrix(), Value(Company, "ABC"), Value(Department, "Finance"), Value(Subgroup, "3"), Value(CostCenter, "CC1001"), Value(JobGrade, "8"))
            .MatchedRuleId.ShouldBe(Id(110));

        Evaluate(L3Matrix(), FinancePerson).MatchedRuleId.ShouldBe(Id(100));

        Evaluate(L3Matrix(), Value(Company, "ABC"), Value(Department, "Finance"), Value(Subgroup, "7"), Value(CostCenter, "CC2000"), Value(JobGrade, "5"))
            .MatchedRuleId.ShouldBe(Id(900));
    }

    [Fact]
    public void ALessSpecificRuleWithHigherPriorityWins()
    {
        var broad = Cond(Company, "Equals", "ABC");
        var narrow = And(Cond(Company, "Equals", "ABC"), Cond(Department, "Equals", "Finance"));
        var result = Evaluate(CompilePolicy(Rule(1, 100, broad), Rule(2, 50, narrow)), Value(Company, "ABC"), Value(Department, "Finance"));
        result.Outcome.ShouldBe(RuleOutcome.Matched);
        result.MatchedRuleId.ShouldBe(Id(1));
    }

    [Fact]
    public void MissingSubgroupHoldsInsteadOfFallingThroughToTheBroadRule()
    {
        // IAM-WF-018 acceptance: a missing EESubgroupCode must not select R-900.
        var result = Evaluate(L3Matrix(), Value(Company, "ABC"), Value(Department, "Finance"), Value(CostCenter, "CC2000"), Value(JobGrade, "5"));
        result.Outcome.ShouldBe(RuleOutcome.HoldMissingOrStaleData);
        result.MatchedRuleId.ShouldBeNull();
    }

    [Fact]
    public void StaleDataHoldsInsteadOfFallingThrough()
    {
        var result = Evaluate(L3Matrix(), Value(Company, "ABC"), Value(Department, "Finance"), Value(Subgroup, "3", Now.AddHours(-3)), Value(CostCenter, "CC2000"), Value(JobGrade, "5"));
        result.Outcome.ShouldBe(RuleOutcome.HoldMissingOrStaleData);
    }

    [Fact]
    public void MissingDataOnAHigherRuleBlocksAMatchingLowerRule() =>
        Evaluate(CompilePolicy(Rule(1, 100, Cond(Department, "Equals", "Finance")), Rule(2, 50, Cond(Company, "Equals", "ABC"))), Value(Company, "ABC"))
            .Outcome.ShouldBe(RuleOutcome.HoldMissingOrStaleData);

    [Fact]
    public void MissingDataCannotReachAWildcardFallback()
    {
        var policy = CompilePolicy(
            Rule(1, 100, And(Cond(Company, "Equals", "ABC"), Cond(CostCenter, "Equals", "CC1001"))),
            Rule(2, 10, And(Cond(Company, "Equals", "ABC"), Any(CostCenter)), wildcard: true));

        Evaluate(policy, Value(Company, "ABC")).Outcome.ShouldBe(RuleOutcome.HoldMissingOrStaleData);
        Evaluate(policy, Value(Company, "ABC"), Value(CostCenter, "CC2000")).MatchedRuleId.ShouldBe(Id(2));
    }

    [Fact]
    public void AFalseHigherRuleWithMissingDataStillFallsThrough()
    {
        // False AND unknown is false: the missing value cannot make this rule true, so the next rule may decide.
        var policy = CompilePolicy(
            Rule(1, 100, And(Cond(Company, "Equals", "XYZ"), Cond(Department, "Equals", "Finance"))),
            Rule(2, 50, Cond(Company, "Equals", "ABC")));
        Evaluate(policy, Value(Company, "ABC")).MatchedRuleId.ShouldBe(Id(2));
    }

    [Fact]
    public void TwoMatchesAtTheSamePriorityHold()
    {
        var policy = CompilePolicy(Rule(1, 50, Cond(Company, "Equals", "ABC")), Rule(2, 50, Cond(Department, "Equals", "Finance")), Rule(3, 10, Cond(Company, "Equals", "ABC")));
        var result = Evaluate(policy, Value(Company, "ABC"), Value(Department, "Finance"));
        result.Outcome.ShouldBe(RuleOutcome.HoldAmbiguous);
        result.MatchedRuleId.ShouldBeNull();
    }

    [Fact]
    public void OneMatchBesideAnUnknownAtTheSamePriorityHolds()
    {
        // The unknown rule might also match, which would be ambiguous: it cannot be settled by assuming it does not.
        var policy = CompilePolicy(Rule(1, 50, Cond(Company, "Equals", "ABC")), Rule(2, 50, Cond(Department, "Equals", "Finance")));
        var result = Evaluate(policy, Value(Company, "ABC"));
        result.Outcome.ShouldBe(RuleOutcome.HoldMissingOrStaleData);
        result.MatchedRuleId.ShouldBeNull();
    }

    [Fact]
    public void AHigherMatchSettlesItAndLowerRulesAreNotRead()
    {
        var policy = CompilePolicy(Rule(1, 100, Cond(Company, "Equals", "ABC")), Rule(2, 50, Cond(Department, "Equals", "Finance")));
        var result = Evaluate(policy, Value(Company, "ABC"));
        result.MatchedRuleId.ShouldBe(Id(1));
        result.Evidence.Rules.Select(r => r.RuleId).ShouldBe([Id(1)]);
    }

    [Fact]
    public void NoMatchHolds() =>
        Evaluate(L3Matrix(), Value(Company, "XYZ"), Value(Department, "HR"), Value(Subgroup, "1"), Value(CostCenter, "C"), Value(JobGrade, "1"))
            .Outcome.ShouldBe(RuleOutcome.HoldNoMatch);

    [Fact]
    public void EffectivePeriodIncludesTheStartAndExcludesTheEnd()
    {
        var node = Cond(Company, "Equals", "ABC");
        var entry = Value(Company, "ABC");
        Evaluate(CompilePolicy(Rule(1, 1, node, from: Now)), entry).Outcome.ShouldBe(RuleOutcome.Matched);
        Evaluate(CompilePolicy(Rule(1, 1, node, from: Now.AddTicks(1))), entry).Outcome.ShouldBe(RuleOutcome.HoldNoMatch);
        Evaluate(CompilePolicy(Rule(1, 1, node, to: Now.AddTicks(1))), entry).Outcome.ShouldBe(RuleOutcome.Matched);
        Evaluate(CompilePolicy(Rule(1, 1, node, to: Now)), entry).Outcome.ShouldBe(RuleOutcome.HoldNoMatch);
    }

    [Fact]
    public void AHigherRuleThatIsNotYetEffectiveDoesNotTakePart()
    {
        var policy = CompilePolicy(Rule(1, 100, Cond(Company, "Equals", "ABC"), from: Now.AddDays(1)), Rule(2, 50, Cond(Company, "Equals", "ABC")));
        Evaluate(policy, Value(Company, "ABC")).MatchedRuleId.ShouldBe(Id(2));
    }

    [Fact]
    public void ExceedingTheStepBudgetHoldsAndNeverMatches()
    {
        var policy = CompilePolicy(Rule(1, 100, And(Cond(Company, "Equals", "XYZ"), Cond(Company, "Equals", "XYZ"))), Rule(2, 50, Cond(Company, "Equals", "ABC")));
        var snapshot = Snapshot(Value(Company, "ABC"));

        // Group + 2 conditions + the second rule's condition is 4 steps.
        PolicyEvaluator.Evaluate(policy, snapshot, Now, 4).MatchedRuleId.ShouldBe(Id(2));
        foreach (var budget in new[] { 3, 2, 1, 0 })
        {
            var result = PolicyEvaluator.Evaluate(policy, snapshot, Now, budget);
            result.Outcome.ShouldBe(RuleOutcome.HoldBudgetExceeded);
            result.MatchedRuleId.ShouldBeNull();
        }
    }

    [Fact]
    public void ReplayGivesByteIdenticalEvidence()
    {
        var first = Evaluate(L3Matrix(), FinancePerson);
        var second = Evaluate(L3Matrix(), [.. FinancePerson.Reverse()]);
        second.Outcome.ShouldBe(first.Outcome);
        second.Evidence.ToCanonicalJson().ShouldBe(first.Evidence.ToCanonicalJson());
    }

    [Fact]
    public void TheResultDependsOnTheGivenTimeNotTheClock()
    {
        var snapshot = Snapshot(Value(Company, "ABC", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var policy = CompilePolicy(Rule(1, 1, Cond(Company, "Equals", "ABC")));
        PolicyEvaluator.Evaluate(policy, snapshot, new DateTimeOffset(2020, 1, 1, 0, 30, 0, TimeSpan.Zero)).Outcome.ShouldBe(RuleOutcome.Matched);
        PolicyEvaluator.Evaluate(policy, snapshot, new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero)).Outcome.ShouldBe(RuleOutcome.HoldMissingOrStaleData);
    }

    [Fact]
    public void ANonUtcEvaluationTimeMeansTheSameMoment()
    {
        var policy = CompilePolicy(Rule(1, 1, Cond(Company, "Equals", "ABC"), from: Now));
        var result = PolicyEvaluator.Evaluate(policy, Snapshot(Value(Company, "ABC")), Now.ToOffset(TimeSpan.FromHours(8)));
        result.Outcome.ShouldBe(RuleOutcome.Matched);
        result.Evidence.ToCanonicalJson().ShouldContain("2026-11-09T08:00:00.0000000Z");
    }

    [Fact]
    public void EvidenceRecordsPolicyRuleAttributeVersionsAndEachCondition()
    {
        var policy = L3Matrix();
        var evidence = Evaluate(policy, FinancePerson).Evidence;
        evidence.PolicyVersionId.ShouldBe(Id(500));
        evidence.PolicyHash.ShouldBe(policy.ContentHash);
        evidence.AsOfUtc.ShouldBe(Now);
        evidence.MatchedRuleId.ShouldBe(Id(100));
        evidence.Attributes.ShouldContain(a => a.AttributeId == Subgroup.AttributeId && a.VersionId == Subgroup.VersionId);
        evidence.Rules.Select(r => r.RuleId).ShouldBe([Id(110), Id(100)]);
        var condition = evidence.Rules[1].Conditions.Single(c => c.AttributeId == Subgroup.AttributeId);
        condition.Test.ShouldBe("In");
        condition.Truth.ShouldBe(RuleTruth.True);
        condition.State.ShouldBe(AttributeValueState.Present);
        condition.Value.ShouldBe("3");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SensitiveValuesNeverEnterEvidenceAsValueOrHash(bool matching)
    {
        const string BandValue = "BAND-SECRET-7";
        const string SalaryValue = "98765.43";
        var policy = CompilePolicy(
            Rule(1, 2, And(Cond(Band, matching ? "Equals" : "NotEquals", BandValue), Cond(Salary, "GreaterThan", "1"))),
            Rule(2, 1, Any(Band), wildcard: true));
        var result = Evaluate(policy, Value(Band, BandValue), Value(Salary, SalaryValue));
        var json = result.Evidence.ToCanonicalJson();

        json.ShouldNotContain(BandValue);
        json.ShouldNotContain(SalaryValue);
        result.Evidence.Rules.SelectMany(r => r.Conditions).ShouldAllBe(c => c.Value == null);
        result.Evidence.Rules.SelectMany(r => r.Conditions).ShouldContain(c => c.State == AttributeValueState.Present);
    }

    [Fact]
    public void InvalidValuesAreNotCopiedIntoEvidence()
    {
        const string Hostile = "<script>not-a-code</script>";
        var json = Evaluate(CompilePolicy(Rule(1, 1, Cond(Company, "Equals", "ABC"))), Value(Company, Hostile)).Evidence.ToCanonicalJson();
        json.ShouldNotContain("script");
        json.ShouldContain("\"state\":\"Invalid\"");
    }

    [Fact]
    public void HoldOutcomesNeverCarryAMatchedRule()
    {
        var holds = new[]
        {
            Evaluate(L3Matrix()),
            Evaluate(CompilePolicy(Rule(1, 1, Cond(Company, "Equals", "A")), Rule(2, 1, Cond(Company, "Equals", "A"))), Value(Company, "A")),
            Evaluate(CompilePolicy(Rule(1, 1, Cond(Company, "Equals", "A"))), Value(Company, "B")),
        };
        foreach (var hold in holds)
        {
            hold.Outcome.ShouldNotBe(RuleOutcome.Matched);
            hold.MatchedRuleId.ShouldBeNull();
            hold.Evidence.MatchedRuleId.ShouldBeNull();
        }
    }
}
