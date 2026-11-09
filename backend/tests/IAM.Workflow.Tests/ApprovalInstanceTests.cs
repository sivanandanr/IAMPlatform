using IAM.Modules.Workflow.Domain.Approvals;
using Shouldly;

namespace IAM.Workflow.Tests;

/// <summary>Synthetic people and routes. Nothing here is a real policy or a real person.</summary>
internal static class F
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);
    public static readonly Guid Requester = Id(901);
    public static readonly Guid Beneficiary = Id(902);
    public static readonly Guid PolicyMaker = Id(903);
    public static readonly Guid A = Id(1);
    public static readonly Guid B = Id(2);
    public static readonly Guid C = Id(3);
    public static readonly Guid D = Id(4);
    public static readonly Guid E = Id(5);
    public static readonly Guid S1 = Id(101);
    public static readonly Guid S2 = Id(102);
    public static readonly Guid S3 = Id(103);

    public static readonly ApprovalBinding Binding = new(Id(201), Id(202), Id(203), new string('a', 64), Id(204), Id(205), Requester, Beneficiary, PolicyMaker);

    public static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    public static StageDefinition Stage(
        Guid id,
        int sequence,
        QuorumMode quorum = QuorumMode.AnyOne,
        int? minimum = null,
        bool mandatory = false,
        bool independent = false,
        bool applicable = true,
        TimeoutAction onTimeout = TimeoutAction.Escalate,
        TimeSpan? deadline = null,
        params Guid[] candidates) =>
        new(id, sequence, quorum, minimum, mandatory, independent, applicable, deadline ?? TimeSpan.FromHours(24), onTimeout, candidates);

    public static ApprovalInstance Start(params StageDefinition[] stages) =>
        ApprovalInstance.Bind(Binding, ApprovalRoute.Create(stages), T0);

    private static int _decisionNumber = 1000;

    public static void Approve(this ApprovalInstance instance, Guid stage, Guid actor, Guid? onBehalfOf = null, DateTimeOffset? at = null) =>
        instance.Decide(new ApprovalDecision(Id(Interlocked.Increment(ref _decisionNumber)), stage, actor, onBehalfOf, true, null, instance.Version), at ?? T0.AddMinutes(1));

    public static void Reject(this ApprovalInstance instance, Guid stage, Guid actor) =>
        instance.Decide(new ApprovalDecision(Id(Interlocked.Increment(ref _decisionNumber)), stage, actor, null, false, "Not justified", instance.Version), T0.AddMinutes(1));

    public static StageView View(this ApprovalInstance instance, Guid stage) => instance.Stages.Single(s => s.StageId == stage);

    public static ApprovalRefusal? Refusal(Action action) => Should.Throw<ApprovalRefusedException>(action).Refusal;
}

public sealed class ApprovalInstanceTests
{
    private static readonly DateTimeOffset T0 = F.T0;

    [Fact]
    public void SequentialStagesRunInOrderAndOnlyTheLastApprovalAuthorizes()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.B]), F.Stage(F.S3, 3, candidates: [F.C]));
        instance.View(F.S1).State.ShouldBe(StageState.Active);
        instance.View(F.S2).State.ShouldBe(StageState.Pending);

        F.Refusal(() => instance.Approve(F.S2, F.B)).ShouldBe(ApprovalRefusal.StageNotActive);

        instance.Approve(F.S1, F.A);
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        instance.State.ShouldBe(ApprovalState.InProgress);
        instance.ProvisioningAuthorized.ShouldBeFalse();

        instance.Approve(F.S2, F.B);
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.Approve(F.S3, F.C);
        instance.State.ShouldBe(ApprovalState.Approved);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AnyNumberOfStagesIsAllowed()
    {
        var stages = Enumerable.Range(1, 10).Select(i => F.Stage(F.Id(100 + i), i, candidates: [F.A])).ToArray();
        var instance = F.Start(stages);
        foreach (var stage in stages)
        {
            instance.ProvisioningAuthorized.ShouldBeFalse();
            instance.Approve(stage.StageId, F.A);
        }

        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AParallelStepAdvancesOnlyAfterBothStages()
    {
        // IAM-WF-004: a Finance + Security parallel stage advances only after both required approvals.
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.B]), F.Stage(F.S3, 2, candidates: [F.C]));
        instance.View(F.S1).State.ShouldBe(StageState.Active);
        instance.View(F.S2).State.ShouldBe(StageState.Active);

        instance.Approve(F.S1, F.A);
        instance.View(F.S1).State.ShouldBe(StageState.Approved);
        instance.View(F.S3).State.ShouldBe(StageState.Pending);

        instance.Approve(F.S2, F.B);
        instance.View(F.S3).State.ShouldBe(StageState.Active);
    }

    [Fact]
    public void AnyOneClosesOnTheFirstEligibleDecision()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A, F.B]), F.Stage(F.S2, 2, candidates: [F.C]));
        instance.Approve(F.S1, F.A);
        instance.View(F.S1).State.ShouldBe(StageState.Approved);
        F.Refusal(() => instance.Approve(F.S1, F.B)).ShouldBe(ApprovalRefusal.StageNotActive);
    }

    [Fact]
    public void AllNeedsEveryEligibleCandidate()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, candidates: [F.A, F.B, F.C]));
        instance.Approve(F.S1, F.A);
        instance.Approve(F.S1, F.B);
        instance.State.ShouldBe(ApprovalState.InProgress);
        instance.Approve(F.S1, F.C);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AllDoesNotWaitForACandidateWhoWasNeverEligible()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, candidates: [F.Requester, F.A, F.B]));
        instance.State.ShouldBe(ApprovalState.InProgress);
        instance.View(F.S1).State.ShouldBe(StageState.Active);

        instance.Approve(F.S1, F.A);
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.Approve(F.S1, F.B);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AfterEscalationAllDoesNotWaitForACandidateWhoWasNeverEligible()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, deadline: TimeSpan.FromHours(1), candidates: [F.A]));
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.Escalate(F.S1, [F.Requester, F.C], F.E, "r", instance.Version, T0.AddHours(2));
        instance.State.ShouldBe(ApprovalState.InProgress);
        instance.View(F.S1).State.ShouldBe(StageState.Active);
        instance.View(F.S1).Required.ShouldBe([F.C]);

        instance.Approve(F.S1, F.C, at: T0.AddHours(2.5));
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void MinimumNNeedsNDistinctPeople()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 2, candidates: [F.A, F.B, F.C]));
        instance.Approve(F.S1, F.A);
        instance.State.ShouldBe(ApprovalState.InProgress);
        F.Refusal(() => instance.Approve(F.S1, F.A)).ShouldBe(ApprovalRefusal.AlreadyDecided);
        instance.State.ShouldBe(ApprovalState.InProgress);
        instance.Approve(F.S1, F.C);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Theory]
    [InlineData(QuorumMode.All, null)]
    [InlineData(QuorumMode.AnyOne, null)]
    [InlineData(QuorumMode.MinimumN, 1)]
    public void AnyEligibleRejectRejectsTheWholeRequest(QuorumMode quorum, int? minimum)
    {
        var instance = F.Start(F.Stage(F.S1, 1, quorum, minimum, candidates: [F.A, F.B]), F.Stage(F.S2, 1, candidates: [F.C]), F.Stage(F.S3, 2, candidates: [F.D]));
        instance.Reject(F.S1, F.A);

        instance.State.ShouldBe(ApprovalState.Rejected);
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.View(F.S1).State.ShouldBe(StageState.Rejected);
        instance.View(F.S2).State.ShouldBe(StageState.Cancelled);
        instance.View(F.S3).State.ShouldBe(StageState.Cancelled);
    }

    [Fact]
    public void ARejectAfterSomeApprovalsStillRejects()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 2, candidates: [F.A, F.B, F.C]));
        instance.Approve(F.S1, F.A);
        instance.Reject(F.S1, F.B);
        instance.State.ShouldBe(ApprovalState.Rejected);
    }

    [Fact]
    public void ARejectNeedsAReason()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        var decision = new ApprovalDecision(F.Id(1), F.S1, F.A, null, false, "  ", instance.Version);
        F.Refusal(() => instance.Decide(decision, T0)).ShouldBe(ApprovalRefusal.InvalidReason);
        F.Refusal(() => instance.Decide(decision with { Reason = null }, T0)).ShouldBe(ApprovalRefusal.InvalidReason);
        instance.State.ShouldBe(ApprovalState.InProgress);
    }

    public static TheoryData<Guid> ExcludedPeople() => [F.Requester, F.Beneficiary, F.PolicyMaker];

    [Theory]
    [MemberData(nameof(ExcludedPeople))]
    public void TheRequesterBeneficiaryAndPolicyMakerCannotApprove(Guid person)
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [person, F.A]));
        instance.View(F.S1).Eligible.ShouldBe([F.A]);
        F.Refusal(() => instance.Approve(F.S1, person)).ShouldBe(ApprovalRefusal.NotEligible);
        instance.Approve(F.S1, F.A);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(ExcludedPeople))]
    public void AnExcludedPersonCannotApproveThroughADelegate(Guid person)
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [person, F.A]));
        F.Refusal(() => instance.Approve(F.S1, F.B, onBehalfOf: person)).ShouldBe(ApprovalRefusal.NotEligible);
    }

    [Theory]
    [MemberData(nameof(ExcludedPeople))]
    public void AnExcludedPersonCannotActAsADelegate(Guid person)
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        F.Refusal(() => instance.Approve(F.S1, person, onBehalfOf: F.A)).ShouldBe(ApprovalRefusal.NotEligible);
    }

    [Theory]
    [MemberData(nameof(ExcludedPeople))]
    public void IfOnlyAnExcludedPersonIsLeftTheStageHoldsAndNeverPasses(Guid person)
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [person]));
        instance.State.ShouldBe(ApprovalState.Held);
        instance.View(F.S1).State.ShouldBe(StageState.Held);
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void ANonCandidateCannotDecide()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        F.Refusal(() => instance.Approve(F.S1, F.B)).ShouldBe(ApprovalRefusal.NotEligible);
        F.Refusal(() => instance.Approve(F.S1, F.B, onBehalfOf: F.C)).ShouldBe(ApprovalRefusal.NotEligible);
    }

    [Fact]
    public void ADelegateCountsOnceAndCannotActForTwoPeople()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 2, candidates: [F.A, F.B, F.C]));
        instance.Approve(F.S1, F.D, onBehalfOf: F.A);
        instance.View(F.S1).Approvers.ShouldBe([F.A]);
        F.Refusal(() => instance.Approve(F.S1, F.D, onBehalfOf: F.B)).ShouldBe(ApprovalRefusal.AlreadyDecided);
        F.Refusal(() => instance.Approve(F.S1, F.A)).ShouldBe(ApprovalRefusal.AlreadyDecided);
        instance.State.ShouldBe(ApprovalState.InProgress);
    }

    [Fact]
    public void ACandidateWhoDelegatedAndADelegateWhoIsACandidateCountOnce()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 2, candidates: [F.A, F.B, F.C]));
        instance.Approve(F.S1, F.B, onBehalfOf: F.A);
        F.Refusal(() => instance.Approve(F.S1, F.B)).ShouldBe(ApprovalRefusal.AlreadyDecided);
    }

    [Fact]
    public void ThePersonRecordedShowsActorAndPrincipal()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        instance.Approve(F.S1, F.D, onBehalfOf: F.A);
        var decision = instance.Decisions.ShouldHaveSingleItem();
        decision.Actor.ShouldBe(F.D);
        decision.Principal.ShouldBe(F.A);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void TheSamePersonMayApproveOrdinaryStages()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.A]));
        instance.Approve(F.S1, F.A);
        instance.Approve(F.S2, F.A);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AnIndependentStageExcludesAnyoneWhoAlreadyDecided()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, independent: true, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.A);
        instance.View(F.S2).Eligible.ShouldBe([F.B]);
        F.Refusal(() => instance.Approve(F.S2, F.A)).ShouldBe(ApprovalRefusal.NotEligible);
        instance.Approve(F.S2, F.B);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void IndependenceWorksWhicheverStageTheyReachFirst()
    {
        var instance = F.Start(F.Stage(F.S1, 1, independent: true, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.A);
        F.Refusal(() => instance.Approve(F.S2, F.A)).ShouldBe(ApprovalRefusal.NotEligible);
    }

    [Fact]
    public void IndependenceAppliesAcrossParallelStagesInEitherOrder()
    {
        var first = F.Start(F.Stage(F.S1, 1, independent: true, candidates: [F.A, F.B]), F.Stage(F.S2, 1, candidates: [F.A]));
        first.Approve(F.S2, F.A);
        F.Refusal(() => first.Approve(F.S1, F.A)).ShouldBe(ApprovalRefusal.NotEligible);
        first.Approve(F.S1, F.B);
        first.ProvisioningAuthorized.ShouldBeTrue();

        var second = F.Start(F.Stage(F.S1, 1, independent: true, candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.A]));
        second.Approve(F.S1, F.A);
        second.State.ShouldBe(ApprovalState.Held);
        second.View(F.S2).State.ShouldBe(StageState.Held);
        second.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void IndependenceCountsADelegatesEarlierDecision()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, independent: true, candidates: [F.D, F.B]));
        instance.Approve(F.S1, F.D, onBehalfOf: F.A);
        instance.View(F.S2).Eligible.ShouldBe([F.B]);
    }

    [Fact]
    public void IndependenceWithNobodyLeftHolds()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, independent: true, candidates: [F.A]));
        instance.Approve(F.S1, F.A);
        instance.State.ShouldBe(ApprovalState.Held);
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void MinimumNHoldsWhenItCanNoLongerBeReached()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 2, candidates: [F.Requester, F.A]));
        instance.State.ShouldBe(ApprovalState.Held);
        instance.View(F.S1).Eligible.ShouldBe([F.A]);
    }

    [Fact]
    public void AllIsFixedWhenTheStageOpensAndHoldsIfSomeoneBecomesIneligible()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, independent: true, candidates: [F.A, F.B]), F.Stage(F.S2, 1, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.B);

        // B decided on the independent stage, so cannot also decide on S2. A can, but then is no longer independent for S1.
        F.Refusal(() => instance.Approve(F.S2, F.B)).ShouldBe(ApprovalRefusal.NotEligible);
        instance.Approve(F.S2, F.A);
        instance.State.ShouldBe(ApprovalState.Held);
        instance.View(F.S1).State.ShouldBe(StageState.Held);
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void AHeldInstanceAcceptsNoDecisionsAnywhere()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.Requester]));
        instance.State.ShouldBe(ApprovalState.Held);
        F.Refusal(() => instance.Approve(F.S1, F.A)).ShouldBe(ApprovalRefusal.InstanceHeld);
    }

    [Fact]
    public void EscalationResolvesAHoldAndTheQuorumStillApplies()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 1, candidates: [F.Requester]), F.Stage(F.S2, 2, candidates: [F.C]));
        instance.State.ShouldBe(ApprovalState.Held);

        instance.Escalate(F.S1, [F.A], F.E, "r", instance.Version, T0.AddHours(1));
        instance.State.ShouldBe(ApprovalState.InProgress);
        instance.View(F.S1).State.ShouldBe(StageState.Active);
        instance.View(F.S1).Escalations.ShouldBe(1);
        instance.ProvisioningAuthorized.ShouldBeFalse();

        instance.Approve(F.S1, F.A, at: T0.AddHours(2));
        instance.View(F.S2).State.ShouldBe(StageState.Active);
    }

    [Fact]
    public void AnEscalationThatCouldNotReopenTheStageIsRefused()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]));
        var version = instance.Version;
        F.Refusal(() => instance.Escalate(F.S1, [F.Beneficiary, F.PolicyMaker], F.E, "r", version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.EscalationCannotReachQuorum);
        instance.Version.ShouldBe(version);
        instance.State.ShouldBe(ApprovalState.Held);
        instance.View(F.S1).Escalations.ShouldBe(0);
        instance.View(F.S1).Candidates.ShouldBe([F.Requester]);
        instance.Escalations.ShouldBeEmpty();
    }

    [Fact]
    public void EarlierApprovalsStillCountAfterEscalationForMinimumN()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 2, deadline: TimeSpan.FromHours(1), candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.A);
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.Escalate(F.S1, [F.C], F.E, "r", instance.Version, T0.AddHours(2));
        instance.View(F.S1).Approvers.ShouldBe([F.A]);
        instance.ProvisioningAuthorized.ShouldBeFalse();
        F.Refusal(() => instance.Approve(F.S1, F.B, at: T0.AddHours(2.5))).ShouldBe(ApprovalRefusal.NotEligible);
        instance.Approve(F.S1, F.C, at: T0.AddHours(2.5));
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(ExcludedPeople))]
    public void TheyCannotChooseTheApproversThemselves(Guid person)
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]));
        F.Refusal(() => instance.Escalate(F.S1, [F.A], person, "r", instance.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.InvalidActor);
        instance.State.ShouldBe(ApprovalState.Held);
    }

    [Fact]
    public void BadEscalationsAreRefused()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]), F.Stage(F.S2, 2, candidates: [F.B]));
        var v = instance.Version;
        F.Refusal(() => instance.Escalate(F.Id(999), [F.C], F.E, "r", v, T0)).ShouldBe(ApprovalRefusal.UnknownStage);
        F.Refusal(() => instance.Escalate(F.S2, [F.C], F.E, "r", v, T0)).ShouldBe(ApprovalRefusal.StageNotEscalatable);
        F.Refusal(() => instance.Escalate(F.S1, [], F.E, "r", v, T0)).ShouldBe(ApprovalRefusal.InvalidCandidates);
        F.Refusal(() => instance.Escalate(F.S1, [F.C, F.C], F.E, "r", v, T0)).ShouldBe(ApprovalRefusal.InvalidCandidates);
        F.Refusal(() => instance.Escalate(F.S1, [Guid.Empty], F.E, "r", v, T0)).ShouldBe(ApprovalRefusal.InvalidCandidates);
        F.Refusal(() => instance.Escalate(F.S1, [F.C], Guid.Empty, "r", v, T0)).ShouldBe(ApprovalRefusal.InvalidActor);
        F.Refusal(() => instance.Escalate(F.S1, [F.C], F.E, null, v, T0)).ShouldBe(ApprovalRefusal.InvalidReason);
        F.Refusal(() => instance.Escalate(F.S1, [F.C], F.E, "  ", v, T0)).ShouldBe(ApprovalRefusal.InvalidReason);
        F.Refusal(() => instance.Escalate(F.S1, [F.C], F.E, "r", v - 1, T0)).ShouldBe(ApprovalRefusal.StaleVersion);
        instance.Version.ShouldBe(v);
        instance.Escalations.ShouldBeEmpty();
        instance.View(F.S1).Candidates.ShouldBe([F.Requester]);
    }

    [Fact]
    public void TimeoutEscalateHoldsAndNeverApproves()
    {
        var instance = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Escalate, deadline: TimeSpan.FromHours(1), candidates: [F.A]));
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.State.ShouldBe(ApprovalState.Held);
        instance.View(F.S1).State.ShouldBe(StageState.Held);
        instance.ProvisioningAuthorized.ShouldBeFalse();

        var version = instance.Version;
        instance.ApplyTimeouts(T0.AddHours(5), version);
        instance.Version.ShouldBe(version);

        instance.Escalate(F.S1, [F.B], F.E, "r", instance.Version, T0.AddHours(6));
        instance.View(F.S1).DeadlineUtc.ShouldBe(T0.AddHours(7));
    }

    [Fact]
    public void TimeoutRejectAndCancelCloseTheRequest()
    {
        var rejecting = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.B]));
        rejecting.ApplyTimeouts(T0.AddHours(2), rejecting.Version);
        rejecting.State.ShouldBe(ApprovalState.Rejected);
        rejecting.View(F.S2).State.ShouldBe(StageState.Cancelled);

        var cancelling = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Cancel, deadline: TimeSpan.FromHours(1), candidates: [F.A]));
        cancelling.ApplyTimeouts(T0.AddHours(2), cancelling.Version);
        cancelling.State.ShouldBe(ApprovalState.Cancelled);
        cancelling.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void BeforeTheDeadlineNothingHappensAndTheExactDeadlineCounts()
    {
        var instance = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.A]));
        var version = instance.Version;
        instance.ApplyTimeouts(T0.AddHours(1).AddTicks(-1), version);
        instance.Version.ShouldBe(version);
        instance.State.ShouldBe(ApprovalState.InProgress);
        instance.ApplyTimeouts(T0.AddHours(1), version);
        instance.State.ShouldBe(ApprovalState.Rejected);
    }

    [Fact]
    public void ALateDecisionIsRefusedEvenBeforeTimeoutsAreProcessed()
    {
        var instance = F.Start(F.Stage(F.S1, 1, deadline: TimeSpan.FromHours(1), candidates: [F.A]));
        F.Refusal(() => instance.Approve(F.S1, F.A, at: T0.AddHours(1))).ShouldBe(ApprovalRefusal.StagePastDeadline);
        instance.Approve(F.S1, F.A, at: T0.AddHours(1).AddTicks(-1));
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AHeldStageWithARejectTimeoutStillRejects()
    {
        var instance = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.Requester]));
        instance.State.ShouldBe(ApprovalState.Held);
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.State.ShouldBe(ApprovalState.Rejected);
    }

    [Fact]
    public void ALaterStagesDeadlineStartsWhenItOpens()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, deadline: TimeSpan.FromHours(1), candidates: [F.B]));
        instance.View(F.S2).DeadlineUtc.ShouldBeNull();
        instance.Approve(F.S1, F.A, at: T0.AddHours(5));
        instance.View(F.S2).DeadlineUtc.ShouldBe(T0.AddHours(6));
    }

    [Fact]
    public void StaleVersionsAndReplaysAreRefused()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 2, candidates: [F.A, F.B, F.C]));
        var original = instance.Version;
        var decision = new ApprovalDecision(F.Id(77), F.S1, F.A, null, true, null, original);
        instance.Decide(decision, T0);

        F.Refusal(() => instance.Decide(decision, T0)).ShouldBe(ApprovalRefusal.StaleVersion);
        F.Refusal(() => instance.Decide(decision with { ExpectedVersion = instance.Version }, T0)).ShouldBe(ApprovalRefusal.DuplicateDecision);
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(78), F.S1, F.B, null, true, null, original), T0)).ShouldBe(ApprovalRefusal.StaleVersion);
        instance.Decisions.Count.ShouldBe(1);
    }

    [Fact]
    public void InvalidDecisionsAreRefused()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        var v = instance.Version;
        F.Refusal(() => instance.Decide(new ApprovalDecision(Guid.Empty, F.S1, F.A, null, true, null, v), T0)).ShouldBe(ApprovalRefusal.InvalidDecision);
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, Guid.Empty, null, true, null, v), T0)).ShouldBe(ApprovalRefusal.InvalidDecision);
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, Guid.Empty, true, null, v), T0)).ShouldBe(ApprovalRefusal.InvalidDecision);
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, F.A, true, null, v), T0)).ShouldBe(ApprovalRefusal.InvalidDecision);
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(1), F.Id(999), F.A, null, true, null, v), T0)).ShouldBe(ApprovalRefusal.UnknownStage);
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, new string('x', ApprovalLimits.MaxReasonLength + 1), v), T0)).ShouldBe(ApprovalRefusal.InvalidReason);
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, "a" + (char)10 + "b", v), T0)).ShouldBe(ApprovalRefusal.InvalidReason);
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, null, v), T0.AddTicks(-1))).ShouldBe(ApprovalRefusal.TimeWentBackwards);
        instance.Version.ShouldBe(v);
        instance.Decisions.ShouldBeEmpty();
        Should.NotThrow(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, new string('x', ApprovalLimits.MaxReasonLength), v), T0));
    }

    [Fact]
    public void OnlyTheRequesterCanWithdraw()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        F.Refusal(() => instance.Withdraw(F.A, null, instance.Version, T0)).ShouldBe(ApprovalRefusal.NotRequester);
        F.Refusal(() => instance.Withdraw(F.Beneficiary, null, instance.Version, T0)).ShouldBe(ApprovalRefusal.NotRequester);
        instance.Withdraw(F.Requester, null, instance.Version, T0);
        instance.State.ShouldBe(ApprovalState.Withdrawn);
        instance.View(F.S1).State.ShouldBe(StageState.Cancelled);
    }

    [Fact]
    public void CancelNeedsAnActorAndAReason()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        F.Refusal(() => instance.Cancel(Guid.Empty, "r", instance.Version, T0)).ShouldBe(ApprovalRefusal.InvalidActor);
        F.Refusal(() => instance.Cancel(F.E, null, instance.Version, T0)).ShouldBe(ApprovalRefusal.InvalidReason);
        instance.Cancel(F.E, "Duplicate request", instance.Version, T0);
        instance.State.ShouldBe(ApprovalState.Cancelled);
    }

    public static TheoryData<string> Closers() => ["Approved", "Rejected", "Cancelled", "Withdrawn", "TimedOutRejected", "TimedOutCancelled"];

    [Theory]
    [MemberData(nameof(Closers))]
    public void AClosedRequestRefusesEveryAction(string how)
    {
        var onTimeout = how == "TimedOutCancelled" ? TimeoutAction.Cancel : TimeoutAction.Reject;
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 1, onTimeout: onTimeout, deadline: TimeSpan.FromHours(1), candidates: [F.A, F.B]));
        switch (how)
        {
            case "Approved":
                instance.Approve(F.S1, F.A);
                break;
            case "Rejected":
                instance.Reject(F.S1, F.A);
                break;
            case "Cancelled":
                instance.Cancel(F.E, "r", instance.Version, T0);
                break;
            case "Withdrawn":
                instance.Withdraw(F.Requester, null, instance.Version, T0);
                break;
            default:
                instance.ApplyTimeouts(T0.AddHours(2), instance.Version);
                break;
        }

        var state = instance.State;
        var version = instance.Version;
        var at = T0.AddDays(1);
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(2), F.S1, F.B, null, true, null, version), at)).ShouldBe(ApprovalRefusal.InstanceClosed);
        F.Refusal(() => instance.ApplyTimeouts(at, version)).ShouldBe(ApprovalRefusal.InstanceClosed);
        F.Refusal(() => instance.Escalate(F.S1, [F.C], F.E, "r", version, at)).ShouldBe(ApprovalRefusal.InstanceClosed);
        F.Refusal(() => instance.Withdraw(F.Requester, null, version, at)).ShouldBe(ApprovalRefusal.InstanceClosed);
        F.Refusal(() => instance.Cancel(F.E, "r", version, at)).ShouldBe(ApprovalRefusal.InstanceClosed);
        instance.State.ShouldBe(state);
        instance.Version.ShouldBe(version);
    }

    [Fact]
    public void AStageThatIsNotApplicableIsRecordedAndSkipped()
    {
        var instance = F.Start(F.Stage(F.S1, 1, applicable: false, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.B]));
        instance.View(F.S1).State.ShouldBe(StageState.NotApplicable);
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        instance.History.ShouldContain(e => e.Kind == ApprovalEventKind.StageNotApplicable && e.StageId == F.S1);
        F.Refusal(() => instance.Approve(F.S1, F.A)).ShouldBe(ApprovalRefusal.StageNotActive);
        instance.Approve(F.S2, F.B);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void ANotApplicableStageInAParallelStepNeverOpens()
    {
        var instance = F.Start(F.Stage(F.S1, 1, applicable: false, candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.B]));
        instance.View(F.S1).State.ShouldBe(StageState.NotApplicable);
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        F.Refusal(() => instance.Approve(F.S1, F.A)).ShouldBe(ApprovalRefusal.StageNotActive);

        instance.Approve(F.S2, F.B);
        instance.View(F.S1).State.ShouldBe(StageState.NotApplicable);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void HistoryIsOrderedAndHoldsNoReasonText()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.B]));
        instance.Approve(F.S1, F.A);
        instance.Reject(F.S2, F.B);
        instance.History.Select(e => e.Version).ShouldBe(Enumerable.Range(1, instance.History.Count).Select(i => (long)i));
        instance.History[0].Kind.ShouldBe(ApprovalEventKind.Bound);
        instance.History[^1].Kind.ShouldBe(ApprovalEventKind.InstanceRejected);
        foreach (var entry in instance.History)
        {
            entry.ToString().ShouldNotContain("Not justified");
        }

        typeof(ApprovalEvent).GetProperties().ShouldNotContain(property => property.PropertyType == typeof(string));
        instance.Decisions[^1].Reason.ShouldBe("Not justified");
    }

    [Fact]
    public void TheSameActionsGiveTheSameHistory()
    {
        ApprovalInstance Run()
        {
            var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.Requester]), F.Stage(F.S3, 2, candidates: [F.C]));
            instance.Escalate(F.S2, [F.B], F.E, "r", instance.Version, T0.AddHours(1));
            instance.Decide(new ApprovalDecision(F.Id(61), F.S1, F.A, null, true, null, instance.Version), T0.AddHours(2));
            instance.Decide(new ApprovalDecision(F.Id(62), F.S2, F.B, null, true, null, instance.Version), T0.AddHours(3));
            instance.Decide(new ApprovalDecision(F.Id(63), F.S3, F.C, null, true, null, instance.Version), T0.AddHours(4));
            return instance;
        }

        Run().History.ShouldBe(Run().History);
    }

    [Fact]
    public void BindingMustBeComplete()
    {
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A])]);
        foreach (var bad in new[]
        {
            F.Binding with { InstanceId = Guid.Empty },
            F.Binding with { RequestItemId = Guid.Empty },
            F.Binding with { PolicyVersionId = Guid.Empty },
            F.Binding with { MatchedRuleId = Guid.Empty },
            F.Binding with { SnapshotReference = Guid.Empty },
            F.Binding with { Requester = Guid.Empty },
            F.Binding with { Beneficiary = Guid.Empty },
            F.Binding with { PolicyMaker = Guid.Empty },
            F.Binding with { PolicyHash = "short" },
            F.Binding with { PolicyHash = new string('A', 64) },
            F.Binding with { PolicyHash = null! },
        })
        {
            F.Refusal(() => ApprovalInstance.Bind(bad, route, T0)).ShouldBe(ApprovalRefusal.InvalidBinding);
        }
    }

    [Fact]
    public void ViewsAndListsCannotChangeTheInstance()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A, F.B]));
        ((ICollection<ApprovalEvent>)instance.History).IsReadOnly.ShouldBeTrue();
        ((ICollection<RecordedDecision>)instance.Decisions).IsReadOnly.ShouldBeTrue();
        ((ICollection<EscalationRecord>)instance.Escalations).IsReadOnly.ShouldBeTrue();
        typeof(ApprovalInstance).GetConstructors().ShouldBeEmpty();
        typeof(ApprovalRoute).GetConstructors().ShouldBeEmpty();

        // Changing what a view hands out must not reach the instance.
        var view = instance.View(F.S1);
        ((ICollection<Guid>)view.Candidates).IsReadOnly.ShouldBeTrue();
        ((ICollection<Guid>)view.Required).IsReadOnly.ShouldBeTrue();
        ((ICollection<Guid>)view.ConfiguredCandidates).IsReadOnly.ShouldBeTrue();
        ReferenceEquals(view.Candidates, instance.View(F.S1).Candidates).ShouldBeFalse();
    }

    [Fact]
    public void AnEscalationKeepsItsOwnCopyOfTheList()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]));
        var list = new[] { F.A };
        instance.Escalate(F.S1, list, F.E, "r", instance.Version, T0);
        list[0] = F.Requester;
        instance.View(F.S1).Candidates.ShouldBe([F.A]);
        instance.Escalations[0].NewCandidates.ShouldBe([F.A]);
    }

    [Fact]
    public void ACallersListCannotChangeARouteAfterItIsBuilt()
    {
        var candidates = new[] { F.A };
        var route = ApprovalRoute.Create([new StageDefinition(F.S1, 1, QuorumMode.AnyOne, null, false, false, true, TimeSpan.FromHours(1), TimeoutAction.Escalate, candidates)]);
        candidates[0] = F.Requester;
        route.Stages[0].Candidates.ShouldBe([F.A]);
    }

    [Fact]
    public void RefusalsDoNotShowPeopleOrReasons()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        var refusal = Should.Throw<ApprovalRefusedException>(() => instance.Approve(F.S1, F.B));
        refusal.Message.ShouldNotContain(F.B.ToString());
        refusal.Message.ShouldContain(nameof(ApprovalRefusal.NotEligible));
    }
}

/// <summary>Rules and regressions from the S2-02 reviews. ADR-0005 records which defects were reproduced by running them.</summary>
public sealed class ApprovalReviewRegressionTests
{
    private static readonly DateTimeOffset T0 = F.T0;

    private static ApprovalInstance HeldAllAfterAApproves(params Guid[] candidates)
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, deadline: TimeSpan.FromHours(1), candidates: candidates));
        instance.Approve(F.S1, F.A);
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.State.ShouldBe(ApprovalState.Held);
        return instance;
    }

    [Fact]
    public void AnActiveStageCannotBeEscalated()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.A);
        F.Refusal(() => instance.Escalate(F.S1, [F.A], F.E, "r", instance.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.StageNotEscalatable);
        instance.State.ShouldBe(ApprovalState.InProgress);
    }

    [Fact]
    public void AnAllListCannotShrinkToThoseWhoAlreadyApproved()
    {
        var instance = HeldAllAfterAApproves(F.A, F.B);
        F.Refusal(() => instance.Escalate(F.S1, [F.A], F.E, "r", instance.Version, T0.AddHours(2))).ShouldBe(ApprovalRefusal.EscalationWouldShrinkAll);
        F.Refusal(() => instance.Escalate(F.S1, [F.C], F.E, "r", instance.Version, T0.AddHours(2))).ShouldBe(ApprovalRefusal.EscalationWouldShrinkAll);
        F.Refusal(() => instance.Escalate(F.S1, [F.Requester, F.C], F.E, "r", instance.Version, T0.AddHours(2))).ShouldBe(ApprovalRefusal.EscalationWouldShrinkAll);
        instance.State.ShouldBe(ApprovalState.Held);
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void AnApproverCannotEscalateTheirOwnStageAfterATimeout()
    {
        var instance = HeldAllAfterAApproves(F.A, F.B);
        F.Refusal(() => instance.Escalate(F.S1, [F.B, F.C], F.A, "r", instance.Version, T0.AddHours(2))).ShouldBe(ApprovalRefusal.EscalatorConflict);
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void ASameListEscalationDoesNotUndoAnIndependenceHold()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, independent: true, candidates: [F.A, F.B]), F.Stage(F.S2, 1, candidates: [F.A]));
        instance.Approve(F.S1, F.B);
        instance.Approve(F.S2, F.A);
        instance.State.ShouldBe(ApprovalState.Held);

        F.Refusal(() => instance.Escalate(F.S1, [F.A, F.B], F.E, "r", instance.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.EscalationCannotReachQuorum);
        instance.State.ShouldBe(ApprovalState.Held);
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void DroppingAnExtraPersonCannotCloseAnAllStageWithNoNewDecision()
    {
        var instance = HeldAllAfterAApproves(F.A, F.B);
        instance.Escalate(F.S1, [F.A, F.B, F.C], F.E, "r", instance.Version, T0.AddHours(2));
        instance.Approve(F.S1, F.B, at: T0.AddHours(2.5));
        instance.ApplyTimeouts(T0.AddHours(3), instance.Version);
        instance.State.ShouldBe(ApprovalState.Held);

        F.Refusal(() => instance.Escalate(F.S1, [F.A, F.B], F.Id(77), "r", instance.Version, T0.AddHours(4))).ShouldBe(ApprovalRefusal.EscalationWouldCloseStage);
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void SomeoneDroppedAfterApprovingCannotBeListedAgain()
    {
        var instance = HeldAllAfterAApproves(F.A, F.B);
        instance.Escalate(F.S1, [F.C, F.D], F.E, "r", instance.Version, T0.AddHours(2));
        instance.ApplyTimeouts(T0.AddHours(3), instance.Version);
        F.Refusal(() => instance.Escalate(F.S1, [F.A, F.D], F.Id(77), "r", instance.Version, T0.AddHours(4))).ShouldBe(ApprovalRefusal.EscalationRelistsEarlierDecider);
        instance.Decisions.ShouldHaveSingleItem();
    }

    [Fact]
    public void ADelegateWhoActedOnAStageCannotBeListedOnItLater()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 2, deadline: TimeSpan.FromHours(1), candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.D, onBehalfOf: F.A);
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        F.Refusal(() => instance.Escalate(F.S1, [F.D, F.C], F.E, "r", instance.Version, T0.AddHours(2))).ShouldBe(ApprovalRefusal.EscalationRelistsEarlierDecider);
    }

    [Fact]
    public void AReplacementsNewDecisionIsWhatClosesAnAllStage()
    {
        var instance = HeldAllAfterAApproves(F.A, F.B);
        instance.Escalate(F.S1, [F.A, F.C], F.E, "r", instance.Version, T0.AddHours(2));
        instance.View(F.S1).State.ShouldBe(StageState.Active);
        instance.View(F.S1).Required.ShouldBe([F.A, F.C]);
        instance.ProvisioningAuthorized.ShouldBeFalse();

        instance.Approve(F.S1, F.C, at: T0.AddHours(2.5));
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void ADroppedApproverNoLongerCountsForAll()
    {
        var instance = HeldAllAfterAApproves(F.A, F.B);
        instance.Escalate(F.S1, [F.C, F.D], F.E, "r", instance.Version, T0.AddHours(2));
        instance.Approve(F.S1, F.C, at: T0.AddHours(2.5));
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.Approve(F.S1, F.D, at: T0.AddHours(2.6));
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AnEscalatorCannotAppointThemselves()
    {
        var instance = F.Start(F.Stage(F.S1, 1, mandatory: true, independent: true, candidates: [F.Requester]));
        F.Refusal(() => instance.Escalate(F.S1, [F.E], F.E, "r", instance.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.EscalatorConflict);
        F.Refusal(() => instance.Escalate(F.S1, [F.A, F.E], F.E, "r", instance.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.EscalatorConflict);
    }

    [Fact]
    public void SomeoneWhoDecidedElsewhereCannotChooseAnIndependentStagesApprovers()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.E]), F.Stage(F.S2, 2, independent: true, candidates: [F.Requester]));
        instance.Approve(F.S1, F.E);
        instance.State.ShouldBe(ApprovalState.Held);
        F.Refusal(() => instance.Escalate(F.S2, [F.A], F.E, "r", instance.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.EscalatorConflict);

        // The same for someone who decided only as a delegate, or only through one.
        var delegated = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.Requester]));
        delegated.Approve(F.S1, F.D, onBehalfOf: F.A);
        F.Refusal(() => delegated.Escalate(F.S2, [F.C], F.D, "r", delegated.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.EscalatorConflict);
        F.Refusal(() => delegated.Escalate(F.S2, [F.C], F.A, "r", delegated.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.EscalatorConflict);
    }

    [Fact]
    public void AnEscalatorCannotDecideLaterOnThisRequest()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]), F.Stage(F.S2, 2, candidates: [F.Beneficiary]));
        instance.Escalate(F.S1, [F.A], F.E, "r", instance.Version, T0.AddHours(1));
        instance.Approve(F.S1, F.A, at: T0.AddHours(2));

        // Another escalator lists E on the next stage: E is on the list, but cannot act, directly or through a delegate.
        instance.Escalate(F.S2, [F.E, F.C], F.Id(77), "r", instance.Version, T0.AddHours(3));
        instance.View(F.S2).Eligible.ShouldBe([F.C]);
        F.Refusal(() => instance.Approve(F.S2, F.E, at: T0.AddHours(4))).ShouldBe(ApprovalRefusal.NotEligible);
        F.Refusal(() => instance.Approve(F.S2, F.C, onBehalfOf: F.E, at: T0.AddHours(4))).ShouldBe(ApprovalRefusal.NotEligible);
        instance.Approve(F.S2, F.C, at: T0.AddHours(4));
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AnAllListingAnEarlierEscalatorCannotReopen()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]), F.Stage(F.S2, 2, QuorumMode.All, candidates: [F.Beneficiary]));
        instance.Escalate(F.S1, [F.A], F.E, "r", instance.Version, T0.AddHours(1));
        instance.Approve(F.S1, F.A, at: T0.AddHours(2));
        F.Refusal(() => instance.Escalate(F.S2, [F.E, F.C], F.Id(77), "r", instance.Version, T0.AddHours(3))).ShouldBe(ApprovalRefusal.EscalationCannotReachQuorum);
    }

    [Fact]
    public void SomeoneOnASiblingsListCannotEscalate()
    {
        // E would make themselves ineligible on their own ALL stage, then use that hold to replace B.
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]), F.Stage(F.S2, 1, QuorumMode.All, mandatory: true, candidates: [F.E, F.B]));
        F.Refusal(() => instance.Escalate(F.S1, [F.A], F.E, "r", instance.Version, T0.AddMinutes(1))).ShouldBe(ApprovalRefusal.EscalatorConflict);
        instance.View(F.S2).State.ShouldBe(StageState.Active);
    }

    [Fact]
    public void SomeoneOnALaterStagesListCannotEscalate()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]), F.Stage(F.S2, 2, QuorumMode.All, mandatory: true, candidates: [F.E, F.B]));
        F.Refusal(() => instance.Escalate(F.S1, [F.A], F.E, "r", instance.Version, T0.AddMinutes(1))).ShouldBe(ApprovalRefusal.EscalatorConflict);
    }

    [Fact]
    public void SomeoneOnlyOnAFinishedStagesListCanEscalateIfTheyNeverDecided()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A, F.E]), F.Stage(F.S2, 2, candidates: [F.Requester]));
        instance.Approve(F.S1, F.A);
        instance.Escalate(F.S2, [F.C], F.E, "r", instance.Version, T0.AddHours(1));
        instance.View(F.S2).State.ShouldBe(StageState.Active);
    }

    [Fact]
    public void AConflictHoldCanReplaceOnlyTheConflictedPerson()
    {
        var instance = F.Start(
            F.Stage(F.S1, 1, QuorumMode.All, mandatory: true, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.A, F.B]),
            F.Stage(F.S2, 1, independent: true, candidates: [F.A]));
        instance.Approve(F.S2, F.A);
        instance.View(F.S1).State.ShouldBe(StageState.Held);

        F.Refusal(() => instance.Escalate(F.S1, [F.C, F.D], F.E, "r", instance.Version, T0.AddMinutes(30))).ShouldBe(ApprovalRefusal.EscalationRemovesAvailableApprover);

        instance.Escalate(F.S1, [F.B, F.C], F.E, "r", instance.Version, T0.AddMinutes(30));
        instance.View(F.S1).State.ShouldBe(StageState.Active);
        instance.View(F.S1).DeadlineUtc.ShouldBe(T0.AddHours(1));
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.State.ShouldBe(ApprovalState.Rejected);
    }

    [Fact]
    public void BeforeTheDeadlineSomeoneWhoApprovedCannotBeDropped()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, independent: true, candidates: [F.A, F.B]), F.Stage(F.S2, 1, candidates: [F.A]));
        instance.Approve(F.S1, F.B);
        instance.Approve(F.S2, F.A);
        F.Refusal(() => instance.Escalate(F.S1, [F.C, F.D], F.E, "r", instance.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.EscalationRemovesAvailableApprover);
        instance.Escalate(F.S1, [F.B, F.C], F.E, "r", instance.Version, T0.AddHours(1));
        instance.Approve(F.S1, F.C, at: T0.AddHours(2));
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void BeforeTheDeadlineADelegateWhoUsedUpTheirSayCanBeReplaced()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.B, onBehalfOf: F.A);
        instance.State.ShouldBe(ApprovalState.Held);
        F.Refusal(() => instance.Escalate(F.S1, [F.B, F.C], F.E, "r", instance.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.EscalationRemovesAvailableApprover);
        instance.Escalate(F.S1, [F.A, F.C], F.E, "r", instance.Version, T0.AddHours(1));
        instance.Approve(F.S1, F.C, at: T0.AddHours(2));
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AnEscalatingStageGetsANewDeadlineWhenItReopens()
    {
        var instance = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Escalate, deadline: TimeSpan.FromHours(1), candidates: [F.Requester]));
        instance.Escalate(F.S1, [F.C], F.E, "r", instance.Version, T0.AddMinutes(30));
        instance.View(F.S1).DeadlineUtc.ShouldBe(T0.AddMinutes(90));
        instance.Escalations[0].DeadlineUtc.ShouldBe(T0.AddMinutes(90));
    }

    [Fact]
    public void AnAllListCanGrowAndThenReturnToItsOpeningSize()
    {
        // Someone listing many people must not raise the size every later list needs.
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, deadline: TimeSpan.FromHours(1), candidates: [F.A, F.B]));
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.Escalate(F.S1, [.. Enumerable.Range(1000, ApprovalLimits.MaxCandidatesPerStage).Select(F.Id)], F.E, "r", instance.Version, T0.AddHours(2));
        instance.ApplyTimeouts(T0.AddHours(3), instance.Version);
        instance.Escalate(F.S1, [F.C, F.D], F.Id(77), "r", instance.Version, T0.AddHours(4));
        instance.View(F.S1).Required.ShouldBe([F.C, F.D]);
    }

    [Theory]
    [InlineData(TimeoutAction.Reject, ApprovalState.Rejected)]
    [InlineData(TimeoutAction.Cancel, ApprovalState.Cancelled)]
    public void AHeldStagePastAClosingDeadlineCannotBeRevived(TimeoutAction action, ApprovalState outcome)
    {
        var instance = F.Start(F.Stage(F.S1, 1, onTimeout: action, deadline: TimeSpan.FromHours(1), candidates: [F.Requester]));
        var version = instance.Version;
        F.Refusal(() => instance.Escalate(F.S1, [F.C], F.E, "r", version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.StagePastDeadline);
        instance.Version.ShouldBe(version);
        instance.ApplyTimeouts(T0.AddHours(1), version);
        instance.State.ShouldBe(outcome);
    }

    [Fact]
    public void AnOverdueActiveStageWithAClosingTimeoutCannotBeRevived()
    {
        var instance = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.A]));
        F.Refusal(() => instance.Escalate(F.S1, [F.C], F.E, "r", instance.Version, T0.AddHours(2))).ShouldBe(ApprovalRefusal.StageNotEscalatable);
        instance.ApplyTimeouts(T0.AddHours(2), instance.Version);
        instance.State.ShouldBe(ApprovalState.Rejected);
    }

    [Fact]
    public void AHeldStageWithAClosingTimeoutCanBeEscalatedBeforeItsDeadline()
    {
        var instance = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.Requester]));
        instance.Escalate(F.S1, [F.C], F.E, "r", instance.Version, T0.AddHours(1).AddTicks(-1));
        instance.View(F.S1).State.ShouldBe(StageState.Active);
        instance.View(F.S1).DeadlineUtc.ShouldBe(T0.AddHours(1));
    }

    [Fact]
    public void AnEscalationThatChangesNothingCannotPostponeARejection()
    {
        var instance = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.Requester]));
        F.Refusal(() => instance.Escalate(F.S1, [F.Beneficiary], F.E, "r", instance.Version, T0.AddMinutes(59))).ShouldBe(ApprovalRefusal.EscalationCannotReachQuorum);
        instance.View(F.S1).DeadlineUtc.ShouldBe(T0.AddHours(1));
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.State.ShouldBe(ApprovalState.Rejected);
    }

    [Fact]
    public void RefusedEscalationsDoNotUseUpTheCap()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]));
        for (var i = 0; i < ApprovalLimits.MaxEscalationsPerStage + 2; i++)
        {
            F.Refusal(() => instance.Escalate(F.S1, [F.Beneficiary], F.E, "r", instance.Version, T0)).ShouldBe(ApprovalRefusal.EscalationCannotReachQuorum);
        }

        instance.Escalate(F.S1, [F.A], F.E, "r", instance.Version, T0);
        instance.View(F.S1).State.ShouldBe(StageState.Active);
    }

    [Fact]
    public void EscalationsPerStageAreCapped()
    {
        var instance = F.Start(F.Stage(F.S1, 1, deadline: TimeSpan.FromHours(1), candidates: [F.A]));
        var at = T0;
        for (var i = 0; i < ApprovalLimits.MaxEscalationsPerStage; i++)
        {
            at = at.AddHours(1);
            instance.ApplyTimeouts(at, instance.Version);
            instance.State.ShouldBe(ApprovalState.Held);
            instance.Escalate(F.S1, [F.A], F.E, "r", instance.Version, at);
        }

        at = at.AddHours(1);
        instance.ApplyTimeouts(at, instance.Version);
        var version = instance.Version;
        F.Refusal(() => instance.Escalate(F.S1, [F.A], F.E, "r", version, at)).ShouldBe(ApprovalRefusal.TooManyEscalations);
        instance.Version.ShouldBe(version);
        instance.View(F.S1).Escalations.ShouldBe(ApprovalLimits.MaxEscalationsPerStage);
    }

    [Fact]
    public void AnAddedIndependentStageHoldsAMandatoryAllInsteadOfShrinkingIt()
    {
        var route = ApprovalRoute.Create([F.Stage(F.S2, 9, QuorumMode.All, mandatory: true, candidates: [F.A, F.B])]);
        var changed = route.WithOverride(new RouteOverride([], [], [F.Stage(F.S3, 1, independent: true, candidates: [F.A])]));
        var instance = ApprovalInstance.Bind(F.Binding, changed, T0);
        instance.Approve(F.S3, F.A);

        instance.State.ShouldBe(ApprovalState.Held);
        instance.View(F.S2).State.ShouldBe(StageState.Held);
        instance.View(F.S2).Required.ShouldBe([F.A, F.B]);
        F.Refusal(() => instance.Approve(F.S2, F.B)).ShouldBe(ApprovalRefusal.InstanceHeld);

        // A replacement resolves it, and both must still approve.
        instance.Escalate(F.S2, [F.B, F.C], F.E, "r", instance.Version, T0.AddHours(1));
        instance.Approve(F.S2, F.B, at: T0.AddHours(2));
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.Approve(F.S2, F.C, at: T0.AddHours(2));
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AnEarlierOrdinaryApproverHoldsALaterIndependentAll()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, QuorumMode.All, independent: true, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.A);
        instance.State.ShouldBe(ApprovalState.Held);
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void AnAllStageListingOnlyExcludedPeopleHolds()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, candidates: [F.Requester, F.Beneficiary]));
        instance.State.ShouldBe(ApprovalState.Held);
        instance.View(F.S1).State.ShouldBe(StageState.Held);
        instance.View(F.S1).Required.ShouldBeEmpty();
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void AnAllMemberWhoActedForSomeoneElseHoldsTheStage()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.B, onBehalfOf: F.A);
        instance.State.ShouldBe(ApprovalState.Held);
        instance.View(F.S1).State.ShouldBe(StageState.Held);
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void MinimumNHoldsWhenADecisionOnTheStageUsesUpThePeopleLeft()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 2, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.B, onBehalfOf: F.A);
        instance.State.ShouldBe(ApprovalState.Held);
    }

    public static TheoryData<DateTimeOffset> BadTimes() =>
    [
        new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero),
        ApprovalLimits.LatestTime,
        new DateTimeOffset(9000, 1, 1, 0, 0, 0, TimeSpan.Zero),
        DateTimeOffset.MaxValue,
        DateTimeOffset.MinValue,
    ];

    [Theory]
    [MemberData(nameof(BadTimes))]
    public void ATimeOutOfRangeIsRefusedAndChangesNothing(DateTimeOffset bad)
    {
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A])]);
        F.Refusal(() => ApprovalInstance.Bind(F.Binding, route, bad)).ShouldBe(ApprovalRefusal.TimeOutOfRange);

        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]), F.Stage(F.S2, 2, candidates: [F.B]));
        var version = instance.Version;
        F.Refusal(() => instance.Escalate(F.S1, [F.C], F.E, "r", version, bad)).ShouldBe(ApprovalRefusal.TimeOutOfRange);
        F.Refusal(() => instance.ApplyTimeouts(bad, version)).ShouldBe(ApprovalRefusal.TimeOutOfRange);
        F.Refusal(() => instance.Cancel(F.E, "r", version, bad)).ShouldBe(ApprovalRefusal.TimeOutOfRange);
        F.Refusal(() => instance.Withdraw(F.Requester, null, version, bad)).ShouldBe(ApprovalRefusal.TimeOutOfRange);
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, null, version), bad)).ShouldBe(ApprovalRefusal.TimeOutOfRange);
        instance.Version.ShouldBe(version);
        instance.View(F.S1).Candidates.ShouldBe([F.Requester]);
    }

    [Fact]
    public void TheLongestDeadlineAtTheLatestTimeDoesNotOverflow()
    {
        var late = ApprovalLimits.LatestTime.AddTicks(-1);
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, deadline: ApprovalLimits.MaxDeadline, candidates: [F.A]), F.Stage(F.S2, 2, deadline: ApprovalLimits.MaxDeadline, candidates: [F.B])]);
        var instance = ApprovalInstance.Bind(F.Binding, route, late);
        instance.Approve(F.S1, F.A, at: late);
        instance.View(F.S2).DeadlineUtc.ShouldBe(late + ApprovalLimits.MaxDeadline);
    }

    [Fact]
    public void AnEscalationRecordsWhoFromWhomToWhomAndWhy()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, deadline: TimeSpan.FromHours(1), candidates: [F.A, F.B]));
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.Escalate(F.S1, [F.C, F.Requester, F.D], F.E, "Approver left", instance.Version, T0.AddHours(2));

        var record = instance.Escalations.ShouldHaveSingleItem();
        record.StageId.ShouldBe(F.S1);
        record.Actor.ShouldBe(F.E);
        record.Reason.ShouldBe("Approver left");
        record.PreviousCandidates.ShouldBe([F.A, F.B]);
        record.NewCandidates.ShouldBe([F.C, F.Requester, F.D]);
        record.PreviousRequired.ShouldBe([F.A, F.B]);
        record.NewRequired.ShouldBe([F.C, F.D]);
        record.DeadlineUtc.ShouldBe(T0.AddHours(3));
        record.AtUtc.ShouldBe(T0.AddHours(2));
        instance.History.TakeLast(3).Select(e => e.Kind).ShouldBe([ApprovalEventKind.StageEscalated, ApprovalEventKind.StageActivated, ApprovalEventKind.InstanceResumed]);

        instance.View(F.S1).ConfiguredCandidates.ShouldBe([F.A, F.B]);
        instance.View(F.S1).Candidates.ShouldBe([F.C, F.Requester, F.D]);
        instance.History.Any(e => e.ToString().Contains("Approver left", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void CancelAndWithdrawKeepTheirReasons()
    {
        var cancelled = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        cancelled.Closure.ShouldBeNull();
        cancelled.Cancel(F.E, "Duplicate request", cancelled.Version, T0);
        cancelled.Closure.ShouldBe(new ApprovalClosure(ApprovalState.Cancelled, F.E, "Duplicate request", T0));

        var withdrawn = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        withdrawn.Withdraw(F.Requester, "No longer needed", withdrawn.Version, T0);
        withdrawn.Closure.ShouldBe(new ApprovalClosure(ApprovalState.Withdrawn, F.Requester, "No longer needed", T0));

        var approved = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        approved.Approve(F.S1, F.A);
        approved.Closure.ShouldBeNull();
    }

    [Fact]
    public void LoggingARecordDoesNotExposeAPersonOrAReason()
    {
        const string Reason = "Sensitive reason text";
        var decision = new ApprovalDecision(F.Id(50), F.S1, F.A, F.B, false, Reason, 1);
        var recorded = new RecordedDecision(F.Id(50), F.S1, F.A, F.B, false, Reason, T0);
        var escalation = new EscalationRecord(F.S1, F.A, Reason, [F.B], [F.C], [F.B], [F.C], T0, T0);
        var closure = new ApprovalClosure(ApprovalState.Cancelled, F.A, Reason, T0);
        var history = new ApprovalEvent(1, T0, ApprovalEventKind.InstanceWithdrawn, null, F.A);
        var binding = F.Binding with { Requester = F.A, Beneficiary = F.B, PolicyMaker = F.C };
        foreach (var text in new[] { decision.ToString(), recorded.ToString(), escalation.ToString(), closure.ToString(), history.ToString(), binding.ToString() })
        {
            text.ShouldNotContain(Reason);
            text.ShouldNotContain(F.A.ToString());
            text.ShouldNotContain(F.B.ToString());
            text.ShouldNotContain(F.C.ToString());
        }
    }

    [Theory]
    [InlineData(0x202E)]
    [InlineData(0x202A)]
    [InlineData(0x2066)]
    [InlineData(0x2069)]
    [InlineData(0x0007)]
    [InlineData(0x061C)]
    [InlineData(0x200E)]
    [InlineData(0x200F)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void AReasonCannotHoldControlCharacters(int character)
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        var reason = "a" + (char)character + "b";
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, false, reason, instance.Version), T0)).ShouldBe(ApprovalRefusal.InvalidReason);
    }

    [Fact]
    public void AnOptionalReasonCannotBeBlankAndNormalScriptsAreAllowed()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, "   ", instance.Version), T0)).ShouldBe(ApprovalRefusal.InvalidReason);

        // Nothing but invisible format characters is blank too.
        var invisible = new string((char)0x200B, 3);
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, false, invisible, instance.Version), T0)).ShouldBe(ApprovalRefusal.InvalidReason);

        // A whole surrogate pair is an ordinary character, even at the end of the text.
        var emoji = "ok" + (char)0xD83D + (char)0xDE00;
        var other = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        Should.NotThrow(() => other.Decide(new ApprovalDecision(F.Id(2), F.S1, F.A, null, true, emoji, other.Version), T0));

        // A zero-width non-joiner is part of normal writing in several languages, and stays allowed.
        var reason = "a" + (char)0x200C + "b";
        Should.NotThrow(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, reason, instance.Version), T0));
    }

    [Fact]
    public void WhenStagesExpireTogetherRejectWinsWhateverTheStageIds()
    {
        ApprovalState Run(Guid cancelStage, Guid rejectStage)
        {
            var instance = F.Start(
                F.Stage(cancelStage, 1, onTimeout: TimeoutAction.Cancel, deadline: TimeSpan.FromHours(1), candidates: [F.A]),
                F.Stage(rejectStage, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.B]));
            instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
            return instance.State;
        }

        Run(F.S1, F.S2).ShouldBe(ApprovalState.Rejected);
        Run(F.S2, F.S1).ShouldBe(ApprovalState.Rejected);
    }

    [Fact]
    public void CancelWinsOverAnEscalatingTimeout()
    {
        var instance = F.Start(
            F.Stage(F.S1, 1, onTimeout: TimeoutAction.Escalate, deadline: TimeSpan.FromHours(1), candidates: [F.A]),
            F.Stage(F.S2, 1, onTimeout: TimeoutAction.Cancel, deadline: TimeSpan.FromHours(1), candidates: [F.B]));
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.State.ShouldBe(ApprovalState.Cancelled);
    }

    [Fact]
    public void WhenAllThreeKindsExpireTogetherRejectWinsAndEachRecordsItsTimeout()
    {
        var instance = F.Start(
            F.Stage(F.S1, 1, onTimeout: TimeoutAction.Escalate, deadline: TimeSpan.FromHours(1), candidates: [F.A]),
            F.Stage(F.S2, 1, onTimeout: TimeoutAction.Cancel, deadline: TimeSpan.FromHours(1), candidates: [F.B]),
            F.Stage(F.S3, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.C]));
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);

        instance.State.ShouldBe(ApprovalState.Rejected);
        instance.View(F.S3).State.ShouldBe(StageState.Rejected);
        instance.View(F.S1).State.ShouldBe(StageState.Cancelled);
        instance.View(F.S2).State.ShouldBe(StageState.Cancelled);
        foreach (var stage in new[] { F.S1, F.S2, F.S3 })
        {
            instance.History.Count(e => e.Kind == ApprovalEventKind.StageTimedOut && e.StageId == stage).ShouldBe(1);
        }
    }

    [Fact]
    public void EveryStageThatExpiredRejectingEndsRejectedWhateverItsId()
    {
        var instance = F.Start(
            F.Stage(F.S1, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.A]),
            F.Stage(F.S2, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.B]));
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.View(F.S1).State.ShouldBe(StageState.Rejected);
        instance.View(F.S2).State.ShouldBe(StageState.Rejected);
        instance.History.Count(e => e.Kind == ApprovalEventKind.StageRejected).ShouldBe(2);
        instance.History.Count(e => e.Kind == ApprovalEventKind.InstanceRejected).ShouldBe(1);
    }

    [Fact]
    public void AnAlreadyHeldEscalatingStageDoesNotRecordItsTimeoutAgainWhenASiblingCloses()
    {
        var instance = F.Start(
            F.Stage(F.S1, 1, onTimeout: TimeoutAction.Escalate, deadline: TimeSpan.FromHours(1), candidates: [F.A]),
            F.Stage(F.S2, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(2), candidates: [F.B]));
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.ApplyTimeouts(T0.AddHours(2), instance.Version);
        instance.State.ShouldBe(ApprovalState.Rejected);
        instance.History.Count(e => e.Kind == ApprovalEventKind.StageTimedOut && e.StageId == F.S1).ShouldBe(1);
    }

    [Fact]
    public void AHoldDoesNotStopASiblingsClock()
    {
        // Recorded as open in DL-045: this pins today's behaviour, which fails closed.
        var instance = F.Start(
            F.Stage(F.S1, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.A]),
            F.Stage(F.S2, 1, candidates: [F.Requester]));
        instance.State.ShouldBe(ApprovalState.Held);
        F.Refusal(() => instance.Approve(F.S1, F.A)).ShouldBe(ApprovalRefusal.InstanceHeld);
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.State.ShouldBe(ApprovalState.Rejected);
    }

    [Theory]
    [MemberData(nameof(ApprovalInstanceTests.ExcludedPeople), MemberType = typeof(ApprovalInstanceTests))]
    public void AnExcludedPersonMayCancel(Guid person)
    {
        // Recorded as open in DL-046: this pins today's behaviour, which grants nothing.
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        instance.Cancel(person, "r", instance.Version, T0);
        instance.State.ShouldBe(ApprovalState.Cancelled);
    }

    [Fact]
    public void TimesWithOtherOffsetsAreComparedAsInstants()
    {
        var late = new DateTimeOffset(2199, 12, 31, 23, 0, 0, TimeSpan.FromHours(-5));
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A])]);
        F.Refusal(() => ApprovalInstance.Bind(F.Binding, route, late)).ShouldBe(ApprovalRefusal.TimeOutOfRange);

        var justInRange = new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.FromHours(5));
        ApprovalInstance.Bind(F.Binding, route, justInRange).View(F.S1).DeadlineUtc!.Value.Offset.ShouldBe(TimeSpan.Zero);

        ApprovalInstance.Bind(F.Binding, route, ApprovalLimits.EarliestTime).State.ShouldBe(ApprovalState.InProgress);

        var instance = F.Start(F.Stage(F.S1, 1, deadline: TimeSpan.FromHours(1), candidates: [F.A]));
        var sameDeadlineElsewhere = T0.AddHours(1).ToOffset(TimeSpan.FromHours(9));
        F.Refusal(() => instance.Approve(F.S1, F.A, at: sameDeadlineElsewhere)).ShouldBe(ApprovalRefusal.StagePastDeadline);
    }

    [Fact]
    public void AParallelAllStepCompletesOnlyWhenEveryoneOnBothStagesHasApproved()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, candidates: [F.A, F.B]), F.Stage(F.S2, 1, QuorumMode.All, candidates: [F.C, F.D]));
        instance.Approve(F.S1, F.A);
        instance.Approve(F.S1, F.B);
        instance.Approve(F.S2, F.C);
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.Approve(F.S2, F.D);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void ARejectByADelegateRejectsTheRequest()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 2, candidates: [F.A, F.B, F.C]));
        instance.Decide(new ApprovalDecision(F.Id(50), F.S1, F.D, F.A, false, "No", instance.Version), T0);
        instance.State.ShouldBe(ApprovalState.Rejected);
    }

    [Fact]
    public void ATimedOutStageRecordsItsFinalState()
    {
        var rejecting = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.A]));
        rejecting.ApplyTimeouts(T0.AddHours(1), rejecting.Version);
        rejecting.History.ShouldContain(e => e.Kind == ApprovalEventKind.StageRejected && e.StageId == F.S1);

        var cancelling = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Cancel, deadline: TimeSpan.FromHours(1), candidates: [F.A]));
        cancelling.ApplyTimeouts(T0.AddHours(1), cancelling.Version);
        cancelling.History.ShouldContain(e => e.Kind == ApprovalEventKind.StageCancelled && e.StageId == F.S1);
    }

    [Fact]
    public void IndependenceCountsThePersonADelegateActedFor()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, independent: true, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.D, onBehalfOf: F.A);
        instance.View(F.S2).Eligible.ShouldBe([F.B]);
    }

    [Fact]
    public void AnOverrideCannotMoveAStageByRemovingAndAddingIt()
    {
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, mandatory: true, candidates: [F.B])]);
        F.Refusal(() => route.WithOverride(new RouteOverride([F.S1], [], [F.Stage(F.S1, 20, candidates: [F.A])]))).ShouldBe(ApprovalRefusal.DuplicateStageId);
    }

    [Fact]
    public void AnOverrideCanRemoveAnOrdinaryStage()
    {
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, mandatory: true, candidates: [F.B])]);
        var changed = route.WithOverride(new RouteOverride([F.S1], [], []));
        changed.Stages.Select(s => s.StageId).ShouldBe([F.S2]);
        route.Stages.Count.ShouldBe(2);
    }

    [Fact]
    public void TheBindingIsKeptAsGiven()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        instance.Binding.ShouldBe(F.Binding);
    }

    [Fact]
    public void ACancelledRequestFromAHeldStateIsClosedAndAuthorizesNothing()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]));
        instance.Cancel(F.E, "r", instance.Version, T0);
        instance.State.ShouldBe(ApprovalState.Cancelled);
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }
}

public sealed class ApprovalRouteTests
{
    private static ApprovalRefusal? Refused(params StageDefinition[] stages) => F.Refusal(() => ApprovalRoute.Create(stages));

    [Fact]
    public void BadRoutesAreRefused()
    {
        F.Refusal(() => ApprovalRoute.Create(null)).ShouldBe(ApprovalRefusal.EmptyRoute);
        Refused().ShouldBe(ApprovalRefusal.EmptyRoute);
        Refused(F.Stage(Guid.Empty, 1, candidates: [F.A])).ShouldBe(ApprovalRefusal.InvalidStageId);
        Refused([null!]).ShouldBe(ApprovalRefusal.InvalidStageId);
        Refused(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S1, 2, candidates: [F.A])).ShouldBe(ApprovalRefusal.DuplicateStageId);
        Refused(F.Stage(F.S1, 0, candidates: [F.A])).ShouldBe(ApprovalRefusal.InvalidSequence);
        Refused(F.Stage(F.S1, -1, candidates: [F.A])).ShouldBe(ApprovalRefusal.InvalidSequence);
        Refused(F.Stage(F.S1, 1)).ShouldBe(ApprovalRefusal.InvalidCandidates);
        Refused(F.Stage(F.S1, 1, candidates: [F.A, F.A])).ShouldBe(ApprovalRefusal.InvalidCandidates);
        Refused(F.Stage(F.S1, 1, candidates: [Guid.Empty])).ShouldBe(ApprovalRefusal.InvalidCandidates);
        Refused(F.Stage(F.S1, 1, deadline: TimeSpan.Zero, candidates: [F.A])).ShouldBe(ApprovalRefusal.InvalidDeadline);
        Refused(F.Stage(F.S1, 1, deadline: TimeSpan.FromSeconds(-1), candidates: [F.A])).ShouldBe(ApprovalRefusal.InvalidDeadline);
        Refused(F.Stage(F.S1, 1, deadline: TimeSpan.MaxValue, candidates: [F.A])).ShouldBe(ApprovalRefusal.InvalidDeadline);
    }

    [Theory]
    [InlineData(QuorumMode.All, 1)]
    [InlineData(QuorumMode.AnyOne, 1)]
    [InlineData(QuorumMode.MinimumN, null)]
    [InlineData(QuorumMode.MinimumN, 0)]
    [InlineData(QuorumMode.MinimumN, -1)]
    [InlineData(QuorumMode.MinimumN, 3)]
    [InlineData((QuorumMode)99, null)]
    public void QuorumSettingsMustMakeSense(QuorumMode quorum, int? minimum) =>
        Refused(F.Stage(F.S1, 1, quorum, minimum, candidates: [F.A, F.B])).ShouldBe(ApprovalRefusal.InvalidQuorum);

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(99)]
    public void TimeoutActionsOutsideTheAllowedThreeDoNotExist(int action) =>
        Refused(F.Stage(F.S1, 1, onTimeout: (TimeoutAction)action, candidates: [F.A])).ShouldBe(ApprovalRefusal.InvalidTimeoutAction);

    [Fact]
    public void TheOnlyTimeoutActionsAreEscalateRejectAndCancel() =>
        Enum.GetNames<TimeoutAction>().ShouldBe(["Escalate", "Reject", "Cancel"], ignoreOrder: true);

    [Fact]
    public void LimitsAreCheckedAtTheBoundary()
    {
        var atLimit = Enumerable.Range(1, ApprovalLimits.MaxStages).Select(i => F.Stage(F.Id(i), i, candidates: [F.A])).ToArray();
        Should.NotThrow(() => ApprovalRoute.Create(atLimit));
        var overLimit = Enumerable.Range(1, ApprovalLimits.MaxStages + 1).Select(i => F.Stage(F.Id(i), i, candidates: [F.A])).ToArray();
        Refused(overLimit).ShouldBe(ApprovalRefusal.TooManyStages);

        var people = Enumerable.Range(1, ApprovalLimits.MaxCandidatesPerStage).Select(F.Id).ToArray();
        Should.NotThrow(() => ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: people)]));
        var tooMany = Enumerable.Range(1, ApprovalLimits.MaxCandidatesPerStage + 1).Select(F.Id).ToArray();
        Refused(F.Stage(F.S1, 1, candidates: tooMany)).ShouldBe(ApprovalRefusal.InvalidCandidates);
    }

    [Fact]
    public void AMandatoryStageCannotBeNotApplicable() =>
        Refused(F.Stage(F.S1, 1, mandatory: true, applicable: false, candidates: [F.A])).ShouldBe(ApprovalRefusal.MandatoryStageNotApplicable);

    [Fact]
    public void ARouteWithNothingToApproveItIsRefused() =>
        Refused(F.Stage(F.S1, 1, applicable: false, candidates: [F.A]), F.Stage(F.S2, 2, applicable: false, candidates: [F.B])).ShouldBe(ApprovalRefusal.NoApplicableStage);

    [Fact]
    public void AnOverrideCannotRemoveOrReplaceAMandatoryStage()
    {
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, mandatory: true, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.B])]);
        F.Refusal(() => route.WithOverride(new RouteOverride([F.S1], [], []))).ShouldBe(ApprovalRefusal.MandatoryStageRemoved);
        F.Refusal(() => route.WithOverride(new RouteOverride([], [F.Stage(F.S1, 1, candidates: [F.C])], []))).ShouldBe(ApprovalRefusal.MandatoryStageReplaced);
        F.Refusal(() => route.WithOverride(new RouteOverride([F.Id(999)], [], []))).ShouldBe(ApprovalRefusal.UnknownStage);
        F.Refusal(() => route.WithOverride(new RouteOverride([], [F.Stage(F.Id(999), 1, candidates: [F.C])], []))).ShouldBe(ApprovalRefusal.UnknownStage);
        route.Stages.Count.ShouldBe(2);
    }

    [Fact]
    public void AnOverrideCanAddStagesAndReplaceOrdinaryOnesAndMandatoryStagesStillRun()
    {
        // IAM-WF-010: a payroll entitlement routes Matrix, then Data Owner, then Compliance; the mandatory stage still applies.
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 9, mandatory: true, candidates: [F.B])]);
        var changed = route.WithOverride(new RouteOverride([], [F.Stage(F.S1, 5, candidates: [F.C])], [F.Stage(F.S3, 2, candidates: [F.D])]));

        changed.Stages.Select(s => s.StageId).ShouldBe([F.S1, F.S3, F.S2]);
        changed.Stages[0].Sequence.ShouldBe(1);
        changed.Stages[0].Candidates.ShouldBe([F.C]);
        route.Stages.Count.ShouldBe(2);

        var instance = ApprovalInstance.Bind(F.Binding, changed, F.T0);
        instance.Approve(F.S1, F.C);
        instance.Approve(F.S3, F.D);
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        instance.Approve(F.S2, F.B);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AnOverrideCannotLeaveNothingToApproveIt()
    {
        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A])]);
        F.Refusal(() => route.WithOverride(new RouteOverride([F.S1], [], []))).ShouldBe(ApprovalRefusal.EmptyRoute);
    }
}

/// <summary>Whatever order actions arrive in, Approved must mean each stage's quorum was truly met by eligible, distinct people.</summary>
public sealed class ApprovalInvariantTests
{
    private static readonly Guid[] People = [F.A, F.B, F.C, F.D, F.E, F.Requester, F.Beneficiary, F.PolicyMaker];

#pragma warning disable CA5394 // A fixed seed on purpose: the generated sequences must be the same on every run.
    [Fact]
    public void GeneratedSequencesNeverReachApprovedUnlessEveryQuorumWasMet()
    {
        var random = new Random(20261109);
        var approved = 0;
        var rejected = 0;
        var cancelled = 0;
        var withdrawn = 0;
        var held = 0;
        var escalated = 0;
        var overridden = 0;
        var approvedWithEscalatedAll = 0;
        var refusals = new Dictionary<ApprovalRefusal, int>();
        for (var round = 0; round < 3_000; round++)
        {
            var stages = RandomStages(random);
            var route = ApprovalRoute.Create(stages);
            if (random.Next(4) == 0)
            {
                route = RandomOverride(random, route, ref overridden);
            }

            var definitions = route.Stages.ToDictionary(s => s.StageId);
            var routeStages = route.Stages.ToArray();
            var instance = ApprovalInstance.Bind(F.Binding, route, F.T0);
            var at = F.T0;
            var decisionNumber = 0;

            for (var step = 0; step < 40 && instance.State is ApprovalState.InProgress or ApprovalState.Held; step++)
            {
                var before = Snapshot(instance);
                at = at.AddMinutes(random.Next(0, 90));
                var when = random.Next(5) == 0 ? at.ToOffset(TimeSpan.FromHours(random.Next(-11, 12))) : at;
                try
                {
                    Act(random, instance, routeStages, when, ref decisionNumber);
                }
                catch (ApprovalRefusedException refusal)
                {
                    refusals[refusal.Refusal!.Value] = refusals.GetValueOrDefault(refusal.Refusal!.Value) + 1;

                    // Nothing visible changes on a refusal.
                    Snapshot(instance).ShouldBe(before);
                }

                held += instance.State == ApprovalState.Held ? 1 : 0;
                CheckInvariants(instance, definitions);
            }

            escalated += instance.Escalations.Count;
            approved += instance.State == ApprovalState.Approved ? 1 : 0;
            rejected += instance.State == ApprovalState.Rejected ? 1 : 0;
            cancelled += instance.State == ApprovalState.Cancelled ? 1 : 0;
            withdrawn += instance.State == ApprovalState.Withdrawn ? 1 : 0;
            if (instance.State == ApprovalState.Approved && instance.Escalations.Any(e => definitions[e.StageId].Quorum == QuorumMode.All))
            {
                approvedWithEscalatedAll++;
            }
        }

        // The generator must really reach every outcome and rule, or this proves nothing.
        approved.ShouldBeGreaterThan(100);
        rejected.ShouldBeGreaterThan(100);
        cancelled.ShouldBeGreaterThan(100);
        withdrawn.ShouldBeGreaterThan(100);
        held.ShouldBeGreaterThan(100);
        escalated.ShouldBeGreaterThan(100);
        overridden.ShouldBeGreaterThan(100);
        approvedWithEscalatedAll.ShouldBeGreaterThan(0);
        foreach (var rule in new[]
        {
            ApprovalRefusal.EscalatorConflict,
            ApprovalRefusal.StagePastDeadline,
            ApprovalRefusal.EscalationWouldShrinkAll,
            ApprovalRefusal.EscalationRemovesAvailableApprover,
            ApprovalRefusal.EscalationCannotReachQuorum,
            ApprovalRefusal.StageNotEscalatable,
            ApprovalRefusal.InstanceHeld,
            ApprovalRefusal.NotEligible,
            ApprovalRefusal.AlreadyDecided,
            ApprovalRefusal.StaleVersion,
        })
        {
            refusals.GetValueOrDefault(rule).ShouldBeGreaterThan(0, rule.ToString());
        }
    }

    private static ApprovalRoute RandomOverride(Random random, ApprovalRoute route, ref int overridden)
    {
        var ordinary = route.Stages.Where(s => !s.Mandatory).ToArray();
        try
        {
            var change = random.Next(3) switch
            {
                0 when ordinary.Length > 0 => new RouteOverride([ordinary[random.Next(ordinary.Length)].StageId], [], []),
                1 when ordinary.Length > 0 => new RouteOverride([], [RandomStage(random, ordinary[random.Next(ordinary.Length)].StageId, mustApply: false)], []),
                _ => new RouteOverride([], [], [RandomStage(random, F.Id(190), mustApply: false)]),
            };
            var changed = route.WithOverride(change);
            overridden++;
            return changed;
        }
        catch (ApprovalRefusedException)
        {
            // For example, removing the only applicable stage. The original route is unchanged.
            return route;
        }
    }

    private static string Snapshot(ApprovalInstance instance)
    {
        var stages = instance.Stages.Select(s => string.Join(
            ';',
            s.StageId,
            s.State,
            s.DeadlineUtc,
            s.Escalations,
            string.Join(',', s.Candidates),
            string.Join(',', s.Required),
            string.Join(',', s.Eligible),
            string.Join(',', s.Approvers)));
        return string.Join('|', instance.State, instance.Version, instance.Decisions.Count, instance.Escalations.Count, instance.Closure, instance.History.Count, string.Join('/', stages));
    }

    private static StageDefinition[] RandomStages(Random random)
    {
        var count = random.Next(1, 5);
        var stages = new StageDefinition[count];
        for (var i = 0; i < count; i++)
        {
            // The first always applies, or the route would be refused for having nothing to approve it.
            stages[i] = RandomStage(random, F.Id(100 + i), mustApply: i == 0);
        }

        return stages;
    }

    private static StageDefinition RandomStage(Random random, Guid id, bool mustApply)
    {
        var candidates = People.OrderBy(_ => random.Next()).Take(random.Next(1, 5)).ToArray();
        var quorum = (QuorumMode)random.Next(1, 4);
        var mandatory = random.Next(3) == 0;
        return new StageDefinition(
            id,
            random.Next(1, 4),
            quorum,
            quorum == QuorumMode.MinimumN ? random.Next(1, candidates.Length + 1) : null,
            mandatory,
            random.Next(3) == 0,
            mustApply || mandatory || random.Next(5) != 0,
            TimeSpan.FromHours(random.Next(1, 8)),
            (TimeoutAction)random.Next(1, 4),
            candidates);
    }

    private static void Act(Random random, ApprovalInstance instance, StageDefinition[] stages, DateTimeOffset at, ref int decisionNumber)
    {
        var stage = stages[random.Next(stages.Length)].StageId;
        var person = People[random.Next(People.Length)];
        switch (random.Next(24))
        {
            case 0:
                instance.ApplyTimeouts(at, instance.Version);
                break;
            case 1:
            case 2:
            case 3:
                // Half from people outside the lists, or nearly every attempt stops at the first check.
                var escalator = random.Next(2) == 0 ? person : F.Id(800 + random.Next(4));
                instance.Escalate(stage, [.. People.OrderBy(_ => random.Next()).Take(random.Next(1, 5))], escalator, "r", instance.Version, at);
                break;
            case 4:
                var forSomeone = random.Next(3) == 0 ? People[random.Next(People.Length)] : (Guid?)null;
                instance.Decide(new ApprovalDecision(F.Id(5000 + decisionNumber++), stage, person, forSomeone, false, "No", instance.Version), at);
                break;
            case 5:
                instance.Decide(new ApprovalDecision(F.Id(5000 + decisionNumber++), stage, person, People[random.Next(People.Length)], true, null, instance.Version), at);
                break;
            case 6:
                instance.Decide(new ApprovalDecision(F.Id(5000 + decisionNumber++), stage, person, null, true, null, instance.Version - random.Next(2)), at);
                break;
            case 7:
                instance.Cancel(person, random.Next(2) == 0 ? "r" : null, instance.Version, at);
                break;
            case 8:
                instance.Withdraw(person, null, instance.Version, at);
                break;
            default:
                instance.Decide(new ApprovalDecision(F.Id(5000 + decisionNumber++), stage, person, null, true, null, instance.Version), at);
                break;
        }
    }

    private static void CheckInvariants(ApprovalInstance instance, Dictionary<Guid, StageDefinition> definitions)
    {
        var excluded = new[] { F.Requester, F.Beneficiary, F.PolicyMaker };

        // Plain LINQ below, not ShouldAllBe: that compiles an expression on every call, and this runs tens of thousands of times.
        // Nobody excluded ever has a decision on record, as actor or principal.
        instance.Decisions.All(d => !excluded.Contains(d.Actor) && !excluded.Contains(d.Principal)).ShouldBeTrue();

        // No person counts twice on a stage.
        foreach (var group in instance.Decisions.GroupBy(d => d.StageId))
        {
            var people = group.SelectMany(d => d.Actor == d.Principal ? [d.Actor] : new[] { d.Actor, d.Principal }).ToList();
            people.Distinct().Count().ShouldBe(people.Count);
        }

        // Independent stages share no person with any other stage.
        foreach (var stage in definitions.Values.Where(s => s.Independent))
        {
            var here = instance.Decisions.Where(d => d.StageId == stage.StageId).SelectMany(d => new[] { d.Actor, d.Principal }).ToHashSet();
            var elsewhere = instance.Decisions.Where(d => d.StageId != stage.StageId).SelectMany(d => new[] { d.Actor, d.Principal });
            here.Overlaps(elsewhere).ShouldBeFalse();
        }

        // Choosing approvers and being one stay separate, and excluded people choose nobody.
        var deciders = instance.Decisions.SelectMany(d => new[] { d.Actor, d.Principal }).ToHashSet();
        instance.Escalations.Any(e => deciders.Contains(e.Actor) || excluded.Contains(e.Actor) || e.NewCandidates.Contains(e.Actor)).ShouldBeFalse();

        // An ALL list never goes below its size at opening, worked out here from the route.
        instance.Escalations.Any(e => definitions[e.StageId].Quorum == QuorumMode.All
            && e.NewCandidates.Count(c => !excluded.Contains(c)) < definitions[e.StageId].Candidates.Count(c => !excluded.Contains(c))).ShouldBeFalse();

        // Nobody is listed again on a stage they decided on after being dropped from it.
        foreach (var escalation in instance.Escalations)
        {
            var deciders2 = instance.Decisions.Where(d => d.StageId == escalation.StageId && d.AtUtc <= escalation.AtUtc).SelectMany(d => new[] { d.Actor, d.Principal });
            deciders2.Any(p => escalation.NewCandidates.Contains(p) && !escalation.PreviousCandidates.Contains(p)).ShouldBeFalse();
        }

        // A stage closes as approved only straight after an approving decision on that same stage. The one thing allowed
        // between them is another stage holding, because that same decision created an independence conflict.
        for (var i = 0; i < instance.History.Count; i++)
        {
            if (instance.History[i].Kind == ApprovalEventKind.StageApproved)
            {
                var previous = i - 1;
                while (instance.History[previous].Kind == ApprovalEventKind.StageHeld)
                {
                    previous--;
                }

                instance.History[previous].Kind.ShouldBe(ApprovalEventKind.DecisionApproved);
                instance.History[previous].StageId.ShouldBe(instance.History[i].StageId);
            }
        }

        // Every counted person was on the stage's list at some point.
        foreach (var decision in instance.Decisions)
        {
            var everListed = definitions[decision.StageId].Candidates.Concat(instance.Escalations.Where(e => e.StageId == decision.StageId).SelectMany(e => e.NewCandidates));
            everListed.Contains(decision.Principal).ShouldBeTrue();
        }

        // Steps run in order: nothing is open while an earlier applicable stage is unfinished.
        var views = instance.Stages;
        foreach (var open in views.Where(s => s.State is StageState.Active or StageState.Held))
        {
            views.Any(s => s.Sequence < open.Sequence && s.State != StageState.Approved && s.State != StageState.NotApplicable).ShouldBeFalse();
        }

        // An active stage always has someone left who can act.
        foreach (var active in views.Where(s => s.State == StageState.Active))
        {
            var decided = instance.Decisions.Where(d => d.StageId == active.StageId).SelectMany(d => new[] { d.Actor, d.Principal }).ToHashSet();
            active.Eligible.Any(c => !decided.Contains(c)).ShouldBeTrue();
        }

        instance.ProvisioningAuthorized.ShouldBe(instance.State == ApprovalState.Approved);
        if (instance.State != ApprovalState.Approved)
        {
            return;
        }

        // Counted again from the decisions alone, without using the state machine's own quorum code.
        instance.Decisions.All(d => d.Approve).ShouldBeTrue();
        foreach (var stage in definitions.Values)
        {
            var view = instance.View(stage.StageId);
            if (!stage.Applicable)
            {
                view.State.ShouldBe(StageState.NotApplicable);
                continue;
            }

            var principals = instance.Decisions.Where(d => d.StageId == stage.StageId).Select(d => d.Principal).ToHashSet();
            var needed = stage.Quorum == QuorumMode.MinimumN ? stage.MinimumApprovals!.Value : 1;
            principals.Count.ShouldBeGreaterThanOrEqualTo(needed);

            if (stage.Quorum == QuorumMode.All)
            {
                // Everyone on the last list, worked out from the route and the escalations given, approved.
                var lastList = instance.Escalations.LastOrDefault(e => e.StageId == stage.StageId)?.NewCandidates ?? stage.Candidates;
                var mustApprove = lastList.Where(c => !excluded.Contains(c)).ToList();
                mustApprove.Count.ShouldBeGreaterThanOrEqualTo(stage.Candidates.Count(c => !excluded.Contains(c)));
                mustApprove.All(principals.Contains).ShouldBeTrue();
            }

            view.State.ShouldBe(StageState.Approved);
        }
    }

    [Fact]
    public void TimeAndEscalationsAloneNeverApproveAnything()
    {
        var random = new Random(7);
        var accepted = 0;
        for (var round = 0; round < 500; round++)
        {
            var stages = RandomStages(random);
            var instance = ApprovalInstance.Bind(F.Binding, ApprovalRoute.Create(stages), F.T0);
            var at = F.T0;

            // Only time passing and escalations, with no decisions: nothing may ever be approved.
            for (var step = 0; step < 15 && instance.State is ApprovalState.InProgress or ApprovalState.Held; step++)
            {
                at = at.AddHours(random.Next(1, 12));
                try
                {
                    if (random.Next(2) == 0)
                    {
                        instance.ApplyTimeouts(at, instance.Version);
                    }
                    else
                    {
                        instance.Escalate(stages[random.Next(stages.Length)].StageId, [.. People.OrderBy(_ => random.Next()).Take(3)], F.Id(800), "r", instance.Version, at);
                        accepted++;
                    }
                }
                catch (ApprovalRefusedException)
                {
                    // Refused is fine; approved is not.
                }

                instance.ProvisioningAuthorized.ShouldBeFalse();
                instance.State.ShouldNotBe(ApprovalState.Approved);
            }

            instance.History.Any(e => e.Kind == ApprovalEventKind.StageApproved).ShouldBeFalse();
        }

        accepted.ShouldBeGreaterThan(50);
    }
#pragma warning restore CA5394
}
