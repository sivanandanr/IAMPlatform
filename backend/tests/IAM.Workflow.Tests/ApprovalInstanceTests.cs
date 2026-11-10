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
        StageKind kind = StageKind.Decision,
        params Guid[] candidates) =>
        new(id, sequence, quorum, minimum, mandatory, independent, applicable, deadline ?? TimeSpan.FromHours(24), onTimeout, candidates, kind);

    public static ApprovalInstance Start(params StageDefinition[] stages) =>
        ApprovalInstance.Bind(Binding, ApprovalRoute.Create(stages), T0);

    private static int _decisionNumber = 1000;

    public static void Approve(this ApprovalInstance instance, Guid stage, Guid actor, Guid? onBehalfOf = null, DateTimeOffset? at = null) =>
        instance.Decide(new ApprovalDecision(Id(Interlocked.Increment(ref _decisionNumber)), stage, actor, onBehalfOf, true, null, instance.Version), at ?? T0.AddMinutes(1));

    public static void Reject(this ApprovalInstance instance, Guid stage, Guid actor) =>
        instance.Decide(new ApprovalDecision(Id(Interlocked.Increment(ref _decisionNumber)), stage, actor, null, false, "Not justified", instance.Version), T0.AddMinutes(1));

    public static void Complete(this ApprovalInstance instance, Guid stage, Guid actor, Guid? onBehalfOf = null, DateTimeOffset? at = null) =>
        instance.CompleteAction(new ActionCompletion(Id(Interlocked.Increment(ref _decisionNumber)), stage, actor, onBehalfOf, null, instance.Version), at ?? T0.AddMinutes(1));

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
        // Mandatory, so the same person is asked at every level rather than approved automatically (DL-057c).
        var stages = Enumerable.Range(1, 10).Select(i => F.Stage(F.Id(100 + i), i, mandatory: true, candidates: [F.A])).ToArray();
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
        // On a mandatory level they decide again; on an ordinary next level it is carried for them (DL-057c).
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, mandatory: true, candidates: [F.A]));
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
    public void AStageThatIsNotApplicableIsRecordedAndNeverOpens()
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
        var recorded = new RecordedDecision(F.Id(50), F.S1, F.A, F.B, RecordedOutcome.Rejected, Reason, T0);
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

/// <summary>Action levels, authorization, Return for changes and approvals carried to the next level (stage 1 of AR-1 to AR-3, DL-055 to DL-057).</summary>
public sealed class ApprovalActionLevelTests
{
    private static readonly DateTimeOffset T0 = F.T0;

    private static StageDefinition Action(Guid id, int sequence, params Guid[] candidates) =>
        F.Stage(id, sequence, kind: StageKind.Action, candidates: candidates);

    [Fact]
    public void AnActionLevelAfterTheLastDecisionRunsAfterAuthorization()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 2, F.B));
        instance.ProvisioningAuthorized.ShouldBeFalse();

        instance.Approve(F.S1, F.A);
        instance.ProvisioningAuthorized.ShouldBeTrue();
        instance.State.ShouldBe(ApprovalState.InProgress);
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        instance.History.Count(e => e.Kind == ApprovalEventKind.InstanceAuthorized).ShouldBe(1);

        instance.CompleteAction(new ActionCompletion(F.Id(50), F.S2, F.B, null, "Created by hand", instance.Version), T0.AddMinutes(2));
        instance.View(F.S2).State.ShouldBe(StageState.Completed);
        instance.Decisions[^1].Reason.ShouldBe("Created by hand");
        instance.State.ShouldBe(ApprovalState.Approved);
        instance.ProvisioningAuthorized.ShouldBeTrue();
        instance.History.Count(e => e.Kind == ApprovalEventKind.InstanceAuthorized).ShouldBe(1);
    }

    [Fact]
    public void AnActionLevelBeforeADecisionAuthorizesNothing()
    {
        var instance = F.Start(Action(F.S1, 1, F.A), F.Stage(F.S2, 2, candidates: [F.B]));
        instance.Complete(F.S1, F.A);
        instance.View(F.S1).State.ShouldBe(StageState.Completed);
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.State.ShouldBe(ApprovalState.InProgress);

        instance.Approve(F.S2, F.B);
        instance.State.ShouldBe(ApprovalState.Approved);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void ARouteWithOnlyActionLevelsIsRefused()
    {
        F.Refusal(() => ApprovalRoute.Create([Action(F.S1, 1, F.A), Action(F.S2, 2, F.B)])).ShouldBe(ApprovalRefusal.NoDecisionStage);

        // A decision level that doesn't apply is no decision either.
        F.Refusal(() => ApprovalRoute.Create([Action(F.S1, 1, F.A), F.Stage(F.S2, 2, applicable: false, candidates: [F.B])])).ShouldBe(ApprovalRefusal.NoDecisionStage);

        var route = ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 2, F.B)]);
        F.Refusal(() => route.WithOverride(new RouteOverride([F.S1], [], []))).ShouldBe(ApprovalRefusal.NoDecisionStage);
        F.Refusal(() => route.WithOverride(new RouteOverride([], [Action(F.S1, 1, F.A)], []))).ShouldBe(ApprovalRefusal.NoDecisionStage);
    }

    [Fact]
    public void AnActionLevelCannotBeApprovedOrRejectedAndADecisionLevelCannotBeCompleted()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 1, F.B));
        var version = instance.Version;
        F.Refusal(() => instance.Approve(F.S2, F.B)).ShouldBe(ApprovalRefusal.WrongActionForStage);
        F.Refusal(() => instance.Reject(F.S2, F.B)).ShouldBe(ApprovalRefusal.WrongActionForStage);
        F.Refusal(() => instance.Complete(F.S1, F.A)).ShouldBe(ApprovalRefusal.WrongActionForStage);
        instance.Version.ShouldBe(version);
        instance.Decisions.ShouldBeEmpty();
    }

    [Fact]
    public void FinishingATaskClosesNoDecisionLevel()
    {
        var instance = F.Start(Action(F.S1, 1, F.A), F.Stage(F.S2, 1, candidates: [F.B]), F.Stage(F.S3, 2, candidates: [F.C]));
        instance.Complete(F.S1, F.A);
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        instance.View(F.S2).Approvers.ShouldBeEmpty();
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.History.ShouldNotContain(e => e.Kind == ApprovalEventKind.StageApproved);
    }

    [Theory]
    [MemberData(nameof(ApprovalInstanceTests.ExcludedPeople), MemberType = typeof(ApprovalInstanceTests))]
    public void ExcludedPeopleCannotCompleteAnActionLevel(Guid person)
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 2, person, F.B));
        instance.Approve(F.S1, F.A);
        instance.View(F.S2).Eligible.ShouldBe([F.B]);
        F.Refusal(() => instance.Complete(F.S2, person)).ShouldBe(ApprovalRefusal.NotEligible);
        F.Refusal(() => instance.Complete(F.S2, F.C, onBehalfOf: person)).ShouldBe(ApprovalRefusal.NotEligible);
        F.Refusal(() => instance.Complete(F.S2, person, onBehalfOf: F.B)).ShouldBe(ApprovalRefusal.NotEligible);
        instance.Complete(F.S2, F.B);
        instance.State.ShouldBe(ApprovalState.Approved);
    }

    [Fact]
    public void AnActionLevelCompletesOutOfOrderOnlyWhenOpen()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 2, F.B));
        F.Refusal(() => instance.Complete(F.S2, F.B)).ShouldBe(ApprovalRefusal.StageNotActive);

        var late = F.Start(Action(F.S1, 1, F.A), F.Stage(F.S2, 2, candidates: [F.B]));
        F.Refusal(() => late.Complete(F.S1, F.A, at: T0.AddHours(24))).ShouldBe(ApprovalRefusal.StagePastDeadline);
    }

    [Fact]
    public void SomeoneWhoFinishedATaskCountsForIndependence()
    {
        var instance = F.Start(Action(F.S1, 1, F.A), F.Stage(F.S2, 2, independent: true, candidates: [F.A, F.B]));
        instance.Complete(F.S1, F.A);
        instance.View(F.S2).Eligible.ShouldBe([F.B]);

        var ordinary = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 2, F.A));
        ordinary.Approve(F.S1, F.A);
        ordinary.Complete(F.S2, F.A);
        ordinary.State.ShouldBe(ApprovalState.Approved);
    }

    [Fact]
    public void SomeoneWhoFinishedATaskCannotEscalate()
    {
        var instance = F.Start(Action(F.S1, 1, F.E), F.Stage(F.S2, 2, candidates: [F.Requester]));
        instance.Complete(F.S1, F.E);
        F.Refusal(() => instance.Escalate(F.S2, [F.A], F.E, "r", instance.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.EscalatorConflict);
    }

    [Fact]
    public void ActionLevelsCanOnlyEscalateOrCancelAtTheirDeadline()
    {
        F.Refusal(() => ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, kind: StageKind.Action, onTimeout: TimeoutAction.Reject, candidates: [F.B])])).ShouldBe(ApprovalRefusal.InvalidTimeoutAction);

        var holding = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, kind: StageKind.Action, deadline: TimeSpan.FromHours(1), candidates: [F.B]));
        holding.Approve(F.S1, F.A);
        holding.ApplyTimeouts(T0.AddHours(2), holding.Version);
        holding.State.ShouldBe(ApprovalState.Held);
        holding.ProvisioningAuthorized.ShouldBeTrue();

        // Escalating keeps the same limits as any other level.
        F.Refusal(() => holding.Escalate(F.S2, [F.A], F.A, "r", holding.Version, T0.AddHours(3))).ShouldBe(ApprovalRefusal.EscalatorConflict);
        holding.Escalate(F.S2, [F.C], F.E, "r", holding.Version, T0.AddHours(3));
        holding.Complete(F.S2, F.C, at: T0.AddHours(3.5));
        holding.State.ShouldBe(ApprovalState.Approved);

        var cancelling = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, kind: StageKind.Action, onTimeout: TimeoutAction.Cancel, deadline: TimeSpan.FromHours(1), candidates: [F.B]));
        cancelling.Approve(F.S1, F.A);
        cancelling.ApplyTimeouts(T0.AddHours(2), cancelling.Version);
        cancelling.State.ShouldBe(ApprovalState.Cancelled);
        cancelling.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Theory]
    [InlineData(QuorumMode.All, null)]
    [InlineData(QuorumMode.MinimumN, 1)]
    public void AnActionLevelIsDoneByOnePerson(QuorumMode quorum, int? minimum) =>
        F.Refusal(() => ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, quorum, minimum, kind: StageKind.Action, candidates: [F.B])])).ShouldBe(ApprovalRefusal.InvalidQuorum);

    [Fact]
    public void CancelAfterAuthorizationWithdrawsIt()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 2, F.B));
        instance.Approve(F.S1, F.A);
        instance.ProvisioningAuthorized.ShouldBeTrue();
        instance.Cancel(F.E, "Duplicate", instance.Version, T0.AddHours(1));
        instance.State.ShouldBe(ApprovalState.Cancelled);
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.History.ShouldContain(e => e.Kind == ApprovalEventKind.InstanceAuthorized);
        instance.History[^1].Kind.ShouldBe(ApprovalEventKind.InstanceCancelled);
    }

    [Fact]
    public void WithdrawIsRefusedAfterAuthorization()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 2, F.B));
        instance.Approve(F.S1, F.A);
        var version = instance.Version;
        F.Refusal(() => instance.Withdraw(F.Requester, null, version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.AlreadyAuthorized);
        instance.Version.ShouldBe(version);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(ApprovalInstanceTests.ExcludedPeople), MemberType = typeof(ApprovalInstanceTests))]
    public void ExcludedPeopleCannotCancelAfterAuthorization(Guid person)
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 2, F.B));
        instance.Approve(F.S1, F.A);
        F.Refusal(() => instance.Cancel(person, "r", instance.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.AlreadyAuthorized);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void ACancelAfterAuthorizationRecordsOpenTasksAsUnknown()
    {
        var instance = F.Start(
            F.Stage(F.S1, 1, candidates: [F.A]),
            F.Stage(F.S2, 2, kind: StageKind.Action, deadline: TimeSpan.FromHours(1), candidates: [F.B]),
            Action(F.S3, 2, F.C),
            Action(F.Id(104), 2, F.D),
            Action(F.Id(105), 3, F.E));
        instance.Approve(F.S1, F.A);
        instance.Complete(F.Id(104), F.D);
        instance.ApplyTimeouts(T0.AddHours(2), instance.Version);
        instance.View(F.S2).State.ShouldBe(StageState.Held);

        instance.Cancel(F.Id(77), "r", instance.Version, T0.AddHours(3));

        // Held and active count; the finished task and the one that never opened do not.
        instance.TasksWithUnknownOutcome.ShouldBe([F.S2, F.S3], ignoreOrder: true);
    }

    [Fact]
    public void ACancelTimeoutAfterAuthorizationIncludesTheTaskThatTimedOut()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, kind: StageKind.Action, onTimeout: TimeoutAction.Cancel, deadline: TimeSpan.FromHours(1), candidates: [F.B]));
        instance.Approve(F.S1, F.A);
        instance.ApplyTimeouts(T0.AddHours(2), instance.Version);
        instance.State.ShouldBe(ApprovalState.Cancelled);
        instance.TasksWithUnknownOutcome.ShouldBe([F.S2]);
    }

    [Fact]
    public void ACancelBeforeAuthorizationHasNoUnknownTasks()
    {
        var instance = F.Start(Action(F.S1, 1, F.A), F.Stage(F.S2, 1, candidates: [F.B]));
        instance.Cancel(F.Requester, "r", instance.Version, T0);
        instance.State.ShouldBe(ApprovalState.Cancelled);
        instance.TasksWithUnknownOutcome.ShouldBeEmpty();
    }

    [Fact]
    public void AfterAuthorizationAHoldDoesNotStopOtherTasksBeingRecorded()
    {
        var instance = F.Start(
            F.Stage(F.S1, 1, candidates: [F.A]),
            F.Stage(F.S2, 2, kind: StageKind.Action, deadline: TimeSpan.FromHours(1), candidates: [F.B]),
            F.Stage(F.S3, 2, kind: StageKind.Action, onTimeout: TimeoutAction.Cancel, deadline: TimeSpan.FromHours(4), candidates: [F.C]));
        instance.Approve(F.S1, F.A);
        instance.ApplyTimeouts(T0.AddHours(2), instance.Version);
        instance.State.ShouldBe(ApprovalState.Held);

        instance.Complete(F.S3, F.C, at: T0.AddHours(3));
        instance.View(F.S3).State.ShouldBe(StageState.Completed);
        instance.State.ShouldBe(ApprovalState.Held);

        // The finished task's clock has no effect now, and the held one still needs someone.
        instance.ApplyTimeouts(T0.AddHours(5), instance.Version);
        instance.State.ShouldBe(ApprovalState.Held);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void BeforeAuthorizationAHoldStillStopsTasks()
    {
        var instance = F.Start(Action(F.S1, 1, F.A), F.Stage(F.S2, 1, candidates: [F.Requester]));
        instance.State.ShouldBe(ApprovalState.Held);
        F.Refusal(() => instance.Complete(F.S1, F.A)).ShouldBe(ApprovalRefusal.InstanceHeld);
    }

    [Fact]
    public void AfterAuthorizationAHeldTaskItselfStillCannotBeCompleted()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, kind: StageKind.Action, deadline: TimeSpan.FromHours(1), candidates: [F.B]));
        instance.Approve(F.S1, F.A);
        instance.ApplyTimeouts(T0.AddHours(2), instance.Version);
        F.Refusal(() => instance.Complete(F.S2, F.B, at: T0.AddHours(3))).ShouldBe(ApprovalRefusal.StageNotActive);
    }

    [Fact]
    public void AMandatoryLevelCannotBeAnActionLevel() =>
        F.Refusal(() => ApprovalRoute.Create([F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, mandatory: true, kind: StageKind.Action, candidates: [F.B])])).ShouldBe(ApprovalRefusal.MandatoryStageCannotBeAction);

    [Fact]
    public void ADelegateCanReturnForChangesAndItIsRecorded()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        instance.ReturnForChanges(F.S1, F.D, F.A, "Needs more", instance.Version, T0);
        instance.State.ShouldBe(ApprovalState.Returned);
        instance.Closure.ShouldBe(new ApprovalClosure(ApprovalState.Returned, F.D, "Needs more", T0, F.A));
    }

    [Fact]
    public void AReturnByADelegateHasTheSameChecksAsADecision()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 2, candidates: [F.Requester, F.A, F.B]));
        var v = instance.Version;
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.D, F.Requester, "r", v, T0)).ShouldBe(ApprovalRefusal.NotEligible);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.Requester, F.A, "r", v, T0)).ShouldBe(ApprovalRefusal.NotEligible);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.D, F.C, "r", v, T0)).ShouldBe(ApprovalRefusal.NotEligible);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.A, F.A, "r", v, T0)).ShouldBe(ApprovalRefusal.InvalidActor);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.A, Guid.Empty, "r", v, T0)).ShouldBe(ApprovalRefusal.InvalidActor);

        instance.Approve(F.S1, F.D, onBehalfOf: F.A);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.D, F.B, "r", instance.Version, T0.AddMinutes(2))).ShouldBe(ApprovalRefusal.AlreadyDecided);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.C, F.A, "r", instance.Version, T0.AddMinutes(2))).ShouldBe(ApprovalRefusal.AlreadyDecided);
    }

    [Theory]
    [InlineData(TimeoutAction.Reject)]
    [InlineData(TimeoutAction.Cancel)]
    public void AReturnCannotPreemptASiblingsOverdueClosingTimeout(TimeoutAction action)
    {
        var instance = F.Start(F.Stage(F.S1, 1, onTimeout: action, deadline: TimeSpan.FromHours(1), candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.B]));
        F.Refusal(() => instance.ReturnForChanges(F.S2, F.B, null, "r", instance.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.StagePastDeadline);
    }

    [Theory]
    [InlineData(TimeoutAction.Reject)]
    [InlineData(TimeoutAction.Cancel)]
    public void AReturnIsAllowedBeforeASiblingsClosingDeadline(TimeoutAction action)
    {
        var instance = F.Start(F.Stage(F.S1, 1, onTimeout: action, deadline: TimeSpan.FromHours(1), candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.B]));
        instance.ReturnForChanges(F.S2, F.B, null, "r", instance.Version, T0.AddHours(1).AddTicks(-1));
        instance.State.ShouldBe(ApprovalState.Returned);
    }

    [Fact]
    public void AReturnIsAllowedBesideASiblingOverdueToEscalate()
    {
        var instance = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Escalate, deadline: TimeSpan.FromHours(1), candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.B]));
        instance.ReturnForChanges(F.S2, F.B, null, "r", instance.Version, T0.AddHours(2));
        instance.State.ShouldBe(ApprovalState.Returned);
    }

    [Fact]
    public void AReturnBesideAnApprovedSiblingCancelsNothingAlreadyApproved()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.B]), F.Stage(F.S3, 2, candidates: [F.C]));
        instance.Approve(F.S1, F.A);
        instance.ReturnForChanges(F.S2, F.B, null, "r", instance.Version, T0.AddHours(1));
        instance.View(F.S1).State.ShouldBe(StageState.Approved);
        instance.View(F.S3).State.ShouldBe(StageState.Cancelled);
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void AFinishedTaskIsRecordedAsCompletedNotApproved()
    {
        var instance = F.Start(Action(F.S1, 1, F.A), F.Stage(F.S2, 2, candidates: [F.B]));
        instance.Complete(F.S1, F.A);
        instance.Decisions[0].Outcome.ShouldBe(RecordedOutcome.Completed);
        instance.View(F.S1).Approvers.ShouldBeEmpty();

        instance.Approve(F.S2, F.B);
        instance.Decisions[1].Outcome.ShouldBe(RecordedOutcome.Approved);
    }

    [Fact]
    public void ADelegateCanFinishATask()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 2, F.B));
        instance.Approve(F.S1, F.A);
        instance.Complete(F.S2, F.D, onBehalfOf: F.B);
        instance.State.ShouldBe(ApprovalState.Approved);
        instance.Decisions[^1].Actor.ShouldBe(F.D);
        instance.Decisions[^1].Principal.ShouldBe(F.B);
    }

    [Fact]
    public void ADecisionIdCannotBeUsedAgainToFinishATask()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 2, F.B));
        instance.Decide(new ApprovalDecision(F.Id(50), F.S1, F.A, null, true, null, instance.Version), T0);
        F.Refusal(() => instance.CompleteAction(new ActionCompletion(F.Id(50), F.S2, F.B, null, null, instance.Version), T0)).ShouldBe(ApprovalRefusal.DuplicateDecision);
    }

    [Fact]
    public void AnIndependentDeciderCannotAlsoFinishATask()
    {
        var instance = F.Start(F.Stage(F.S1, 1, independent: true, candidates: [F.A]), Action(F.S2, 2, F.A, F.B));
        instance.Approve(F.S1, F.A);
        F.Refusal(() => instance.Complete(F.S2, F.A)).ShouldBe(ApprovalRefusal.NotEligible);
        instance.Complete(F.S2, F.B);
        instance.State.ShouldBe(ApprovalState.Approved);
    }

    [Fact]
    public void ReachingAuthorizationAndClosingTogetherRecordInTheRightOrder()
    {
        var first = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 1, F.B));
        first.Complete(F.S2, F.B);
        first.Approve(F.S1, F.A);
        first.History.Select(e => e.Kind).TakeLast(2).ShouldBe([ApprovalEventKind.InstanceAuthorized, ApprovalEventKind.InstanceApproved]);

        // Authorized, then held: the same order wherever the action level sits.
        var held = F.Start(F.Stage(F.S1, 1, independent: true, candidates: [F.A]), Action(F.S2, 1, F.A));
        held.Approve(F.S1, F.A);
        var kinds = held.History.Select(e => e.Kind).ToList();
        kinds.IndexOf(ApprovalEventKind.InstanceAuthorized).ShouldBeLessThan(kinds.IndexOf(ApprovalEventKind.InstanceHeld));
    }

    [Fact]
    public void AuthorizationIsRecordedOnceWhoeverActsFirst()
    {
        var approvedFirst = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 1, F.B));
        approvedFirst.Approve(F.S1, F.A);
        approvedFirst.Complete(F.S2, F.B);

        var completedFirst = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 1, F.B));
        completedFirst.Complete(F.S2, F.B);
        completedFirst.Approve(F.S1, F.A);

        foreach (var instance in new[] { approvedFirst, completedFirst })
        {
            instance.History.Count(e => e.Kind == ApprovalEventKind.InstanceAuthorized).ShouldBe(1);
        }
    }

    [Fact]
    public void ActionAndDecisionLevelsExpiringTogetherGiveTheSameResultWhateverTheIds()
    {
        ApprovalInstance Run(Guid decision, Guid action)
        {
            var instance = F.Start(
                F.Stage(decision, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.A]),
                F.Stage(action, 1, kind: StageKind.Action, onTimeout: TimeoutAction.Cancel, deadline: TimeSpan.FromHours(1), candidates: [F.B]));
            instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
            return instance;
        }

        foreach (var (decision, action) in new[] { (F.S1, F.S2), (F.S2, F.S1) })
        {
            var instance = Run(decision, action);
            instance.State.ShouldBe(ApprovalState.Rejected);
            instance.View(decision).State.ShouldBe(StageState.Rejected);
            instance.View(action).State.ShouldBe(StageState.Cancelled);
        }
    }

    [Theory]
    [InlineData(StageState.Completed)]
    [InlineData(StageState.NotApplicable)]
    public void AReturnIsRefusedOnALevelThatIsNotOpen(StageState state)
    {
        var first = state switch
        {
            StageState.Completed => Action(F.S1, 1, F.A),
            _ => F.Stage(F.S1, 1, applicable: false, candidates: [F.A]),
        };
        var instance = F.Start(first, F.Stage(F.S2, 2, candidates: [F.B]));
        if (state == StageState.Completed)
        {
            instance.Complete(F.S1, F.A);
        }

        instance.View(F.S1).State.ShouldBe(state);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.A, null, "r", instance.Version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.StageNotActive);
    }

    [Fact]
    public void ARouteWithNoActionLevelsHasTheSameHistoryAsBefore()
    {
        ApprovalEventKind[] Kinds(ApprovalInstance instance) => [.. instance.History.Select(e => e.Kind)];

        var single = F.Start(F.Stage(F.S1, 1, candidates: [F.A]));
        single.Approve(F.S1, F.A);
        Kinds(single).ShouldBe([ApprovalEventKind.Bound, ApprovalEventKind.StageActivated, ApprovalEventKind.DecisionApproved, ApprovalEventKind.StageApproved, ApprovalEventKind.InstanceApproved]);

        var sequential = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.B]));
        sequential.Approve(F.S1, F.A);
        sequential.Approve(F.S2, F.B);
        Kinds(sequential).ShouldBe([ApprovalEventKind.Bound, ApprovalEventKind.StageActivated, ApprovalEventKind.DecisionApproved, ApprovalEventKind.StageApproved, ApprovalEventKind.StageActivated, ApprovalEventKind.DecisionApproved, ApprovalEventKind.StageApproved, ApprovalEventKind.InstanceApproved]);

        var parallel = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.B]));
        parallel.Approve(F.S1, F.A);
        parallel.Approve(F.S2, F.B);
        Kinds(parallel).ShouldBe([ApprovalEventKind.Bound, ApprovalEventKind.StageActivated, ApprovalEventKind.StageActivated, ApprovalEventKind.DecisionApproved, ApprovalEventKind.StageApproved, ApprovalEventKind.DecisionApproved, ApprovalEventKind.StageApproved, ApprovalEventKind.InstanceApproved]);

        var notApplicable = F.Start(F.Stage(F.S1, 1, applicable: false, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.B]));
        notApplicable.Approve(F.S2, F.B);
        Kinds(notApplicable).ShouldBe([ApprovalEventKind.Bound, ApprovalEventKind.StageNotApplicable, ApprovalEventKind.StageActivated, ApprovalEventKind.DecisionApproved, ApprovalEventKind.StageApproved, ApprovalEventKind.InstanceApproved]);

        var escalated = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]));
        escalated.Escalate(F.S1, [F.A], F.E, "r", escalated.Version, T0);
        escalated.Approve(F.S1, F.A);
        Kinds(escalated).ShouldBe([ApprovalEventKind.Bound, ApprovalEventKind.StageActivated, ApprovalEventKind.StageHeld, ApprovalEventKind.InstanceHeld, ApprovalEventKind.StageEscalated, ApprovalEventKind.StageActivated, ApprovalEventKind.InstanceResumed, ApprovalEventKind.DecisionApproved, ApprovalEventKind.StageApproved, ApprovalEventKind.InstanceApproved]);

        var timedOut = F.Start(F.Stage(F.S1, 1, onTimeout: TimeoutAction.Reject, deadline: TimeSpan.FromHours(1), candidates: [F.A]));
        timedOut.ApplyTimeouts(T0.AddHours(1), timedOut.Version);
        Kinds(timedOut).ShouldBe([ApprovalEventKind.Bound, ApprovalEventKind.StageActivated, ApprovalEventKind.StageTimedOut, ApprovalEventKind.StageRejected, ApprovalEventKind.InstanceRejected]);
    }

    [Fact]
    public void AnEmptyListIsRefused() =>
        F.Refusal(() => ApprovalRoute.Create([F.Stage(F.S1, 1)])).ShouldBe(ApprovalRefusal.InvalidCandidates);

    [Fact]
    public void AnIndependenceConflictOnALaterLevelHolds()
    {
        var instance = F.Start(F.Stage(F.S1, 1, independent: true, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.A]));
        instance.Approve(F.S1, F.A);
        instance.View(F.S2).State.ShouldBe(StageState.Held);
    }

    [Fact]
    public void AnUnknownKindOrSettingIsRefused()
    {
        F.Refusal(() => ApprovalRoute.Create([F.Stage(F.S1, 1, kind: (StageKind)9, candidates: [F.A])])).ShouldBe(ApprovalRefusal.InvalidStageSetting);
    }

    [Fact]
    public void ReturnForChangesClosesTheRequestAndAuthorizesNothing()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.B]), Action(F.S3, 3, F.C));
        instance.Approve(F.S1, F.A);
        instance.ReturnForChanges(F.S2, F.B, null, "Wrong entitlement", instance.Version, T0.AddHours(1));

        instance.State.ShouldBe(ApprovalState.Returned);
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.View(F.S2).State.ShouldBe(StageState.Returned);
        instance.View(F.S3).State.ShouldBe(StageState.Cancelled);
        instance.Closure.ShouldBe(new ApprovalClosure(ApprovalState.Returned, F.B, "Wrong entitlement", T0.AddHours(1)));
        instance.History.TakeLast(3).Select(e => e.Kind).ShouldBe([ApprovalEventKind.StageReturned, ApprovalEventKind.StageCancelled, ApprovalEventKind.InstanceReturned]);
        instance.History.Any(e => e.ToString().Contains("Wrong entitlement", StringComparison.Ordinal)).ShouldBeFalse();

        var version = instance.Version;
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(50), F.S2, F.B, null, true, null, version), T0.AddHours(2))).ShouldBe(ApprovalRefusal.InstanceClosed);
        F.Refusal(() => instance.ReturnForChanges(F.S2, F.B, null, "r", version, T0.AddHours(2))).ShouldBe(ApprovalRefusal.InstanceClosed);
    }

    [Fact]
    public void AnActionLevelBeforeTheLastDecisionCanReturnForChanges()
    {
        var instance = F.Start(Action(F.S1, 1, F.A), F.Stage(F.S2, 2, candidates: [F.B]));
        instance.ReturnForChanges(F.S1, F.A, null, "Name needs more than a rename", instance.Version, T0);
        instance.State.ShouldBe(ApprovalState.Returned);
    }

    [Fact]
    public void ReturnForChangesIsRefusedAfterAuthorization()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), Action(F.S2, 2, F.B));
        instance.Approve(F.S1, F.A);
        var version = instance.Version;
        F.Refusal(() => instance.ReturnForChanges(F.S2, F.B, null, "r", version, T0.AddHours(1))).ShouldBe(ApprovalRefusal.AlreadyAuthorized);
        instance.Version.ShouldBe(version);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void ReturnForChangesNeedsSomeoneAbleToActAndAReason()
    {
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.MinimumN, 2, deadline: TimeSpan.FromHours(1), candidates: [F.Requester, F.A, F.B]), F.Stage(F.S2, 2, candidates: [F.C]));
        var v = instance.Version;
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.Requester, null, "r", v, T0)).ShouldBe(ApprovalRefusal.NotEligible);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.C, null, "r", v, T0)).ShouldBe(ApprovalRefusal.NotEligible);
        F.Refusal(() => instance.ReturnForChanges(F.S1, Guid.Empty, null, "r", v, T0)).ShouldBe(ApprovalRefusal.InvalidActor);
        F.Refusal(() => instance.ReturnForChanges(F.S2, F.C, null, "r", v, T0)).ShouldBe(ApprovalRefusal.StageNotActive);
        F.Refusal(() => instance.ReturnForChanges(F.Id(999), F.A, null, "r", v, T0)).ShouldBe(ApprovalRefusal.UnknownStage);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.A, null, null, v, T0)).ShouldBe(ApprovalRefusal.InvalidReason);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.A, null, "  ", v, T0)).ShouldBe(ApprovalRefusal.InvalidReason);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.A, null, "r", v - 1, T0)).ShouldBe(ApprovalRefusal.StaleVersion);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.A, null, "r", v, T0.AddHours(1))).ShouldBe(ApprovalRefusal.StagePastDeadline);
        instance.Version.ShouldBe(v);
        instance.Closure.ShouldBeNull();

        instance.Approve(F.S1, F.A);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.A, null, "r", instance.Version, T0.AddMinutes(2))).ShouldBe(ApprovalRefusal.AlreadyDecided);
    }

    [Fact]
    public void ReturnForChangesIsRefusedDuringAHold()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.Requester]));
        instance.State.ShouldBe(ApprovalState.Held);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.A, null, "r", instance.Version, T0)).ShouldBe(ApprovalRefusal.InstanceHeld);
    }

    [Fact]
    public void LoggingACompletionDoesNotExposeAPersonOrANote()
    {
        var text = new ActionCompletion(F.Id(50), F.S1, F.A, F.B, "Sensitive note", 1).ToString();
        text.ShouldNotContain("Sensitive note");
        text.ShouldNotContain(F.A.ToString());
        text.ShouldNotContain(F.B.ToString());
    }
}

/// <summary>The same person at the next decision level is approved automatically, within the limits of DL-057(c) and DL-058.</summary>
public sealed class ApprovalSameApproverTests
{
    private static readonly DateTimeOffset T0 = F.T0;

    [Fact]
    public void TheNextLevelIsApprovedForTheSamePersonWithARemarkAndALink()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.A);

        instance.State.ShouldBe(ApprovalState.Approved);
        var carried = instance.Decisions.Single(d => d.StageId == F.S2);
        carried.Outcome.ShouldBe(RecordedOutcome.ApprovedAutomatically);
        carried.Principal.ShouldBe(F.A);
        carried.Actor.ShouldBe(F.A);
        carried.Reason.ShouldBe(ApprovalInstance.SameApproverRemark);
        carried.CarriedFrom.ShouldBe(instance.Decisions.Single(d => d.StageId == F.S1).DecisionId);
        instance.History.ShouldContain(e => e.Kind == ApprovalEventKind.DecisionApprovedAutomatically && e.StageId == F.S2 && e.Actor == F.A);
    }

    [Fact]
    public void ItCountsAsOneApprovalSoOthersAreStillNeeded()
    {
        var all = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, QuorumMode.All, candidates: [F.A, F.B]));
        all.Approve(F.S1, F.A);
        all.View(F.S2).Approvers.ShouldBe([F.A]);
        all.ProvisioningAuthorized.ShouldBeFalse();
        all.Approve(F.S2, F.B);
        all.ProvisioningAuthorized.ShouldBeTrue();

        var two = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, QuorumMode.MinimumN, 2, candidates: [F.A, F.B, F.C]));
        two.Approve(F.S1, F.A);
        two.ProvisioningAuthorized.ShouldBeFalse();
        F.Refusal(() => two.Approve(F.S2, F.A)).ShouldBe(ApprovalRefusal.AlreadyDecided);
        two.Approve(F.S2, F.C);
        two.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AMandatoryLevelIsNeverApprovedAutomatically()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, mandatory: true, candidates: [F.A]));
        instance.Approve(F.S1, F.A);
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.Approve(F.S2, F.A);
        instance.Decisions[^1].Outcome.ShouldBe(RecordedOutcome.Approved);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void AnIndependentLevelIsNeverApprovedAutomatically()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, independent: true, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.A);
        instance.View(F.S2).Approvers.ShouldBeEmpty();
        F.Refusal(() => instance.Approve(F.S2, F.A)).ShouldBe(ApprovalRefusal.NotEligible);
        instance.Approve(F.S2, F.B);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void ApprovingAnIndependentLevelCarriesNothingToTheNext()
    {
        var instance = F.Start(F.Stage(F.S1, 1, independent: true, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.A);
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        instance.View(F.S2).Approvers.ShouldBeEmpty();
    }

    [Fact]
    public void ADelegatesDecisionIsNotCarried()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.A, F.D]));
        instance.Approve(F.S1, F.D, onBehalfOf: F.A);
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        instance.View(F.S2).Approvers.ShouldBeEmpty();
    }

    [Fact]
    public void OnlyTheNextDecisionLevelNotOneFurtherOn()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.B]), F.Stage(F.S3, 3, candidates: [F.A, F.C]));
        instance.Approve(F.S1, F.A);
        instance.Approve(F.S2, F.B);
        instance.View(F.S3).State.ShouldBe(StageState.Active);
        instance.View(F.S3).Approvers.ShouldBeEmpty();
    }

    [Fact]
    public void LevelsThatDoNotApplyArePassedOver()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.Id(104), 2, applicable: false, candidates: [F.C]), F.Stage(F.S3, 3, candidates: [F.A, F.C]));
        instance.Approve(F.S1, F.A);
        instance.View(F.S3).State.ShouldBe(StageState.Approved);
        instance.Decisions[^1].Outcome.ShouldBe(RecordedOutcome.ApprovedAutomatically);
    }

    [Fact]
    public void ATaskInBetweenStopsTheCarry()
    {
        // The second decision is there to look at what the task did, so the person decides again (DL-058).
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, kind: StageKind.Action, candidates: [F.B]), F.Stage(F.S3, 3, candidates: [F.A, F.C]));
        instance.Approve(F.S1, F.A);
        instance.Complete(F.S2, F.B);
        instance.View(F.S3).State.ShouldBe(StageState.Active);
        instance.View(F.S3).Approvers.ShouldBeEmpty();
    }

    [Fact]
    public void ATaskBesideThePreviousDecisionStopsTheCarryToo()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 1, kind: StageKind.Action, candidates: [F.B]), F.Stage(F.S3, 2, candidates: [F.A, F.C]));
        instance.Approve(F.S1, F.A);
        instance.Complete(F.S2, F.B);
        instance.View(F.S3).Approvers.ShouldBeEmpty();
    }

    [Fact]
    public void ATaskThatDoesNotApplyDoesNotStopIt()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, kind: StageKind.Action, applicable: false, candidates: [F.B]), F.Stage(F.S3, 3, candidates: [F.A, F.C]));
        instance.Approve(F.S1, F.A);
        instance.View(F.S3).State.ShouldBe(StageState.Approved);
    }

    [Fact]
    public void ATaskIsNotCarriedAsAnApproval()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.B]), F.Stage(F.S2, 2, kind: StageKind.Action, candidates: [F.A]), F.Stage(F.S3, 3, candidates: [F.A, F.C]));
        instance.Approve(F.S1, F.B);
        instance.Complete(F.S2, F.A);
        instance.View(F.S3).Approvers.ShouldBeEmpty();
    }

    [Fact]
    public void ItIsNeverCarriedToSomeoneNotListed()
    {
        var notListed = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.B]));
        notListed.Approve(F.S1, F.A);
        notListed.View(F.S2).Approvers.ShouldBeEmpty();
    }

    [Fact]
    public void ItDoesNotChainBeyondTheNextLevel()
    {
        // Only a real decision on the level just before carries, never another automatic approval (DL-058).
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.A]), F.Stage(F.S3, 3, candidates: [F.A]));
        instance.Approve(F.S1, F.A);
        instance.View(F.S2).State.ShouldBe(StageState.Approved);
        instance.Decisions.Single(d => d.StageId == F.S2).CarriedFrom.ShouldBe(instance.Decisions.Single(d => d.StageId == F.S1).DecisionId);
        instance.View(F.S3).State.ShouldBe(StageState.Active);
        instance.View(F.S3).Approvers.ShouldBeEmpty();

        instance.Approve(F.S3, F.A);
        instance.Decisions[^1].Outcome.ShouldBe(RecordedOutcome.Approved);
        instance.State.ShouldBe(ApprovalState.Approved);
    }

    [Fact]
    public void NothingIsCarriedFromALevelThatWasEscalated()
    {
        // A's approval didn't count there once the list was replaced, so it can't carry either (DL-058).
        var instance = F.Start(F.Stage(F.S1, 1, QuorumMode.All, deadline: TimeSpan.FromHours(1), candidates: [F.A, F.B]), F.Stage(F.S2, 2, candidates: [F.A, F.Id(30)]));
        instance.Approve(F.S1, F.A);
        instance.ApplyTimeouts(T0.AddHours(1), instance.Version);
        instance.Escalate(F.S1, [F.C, F.D], F.E, "r", instance.Version, T0.AddHours(2));
        instance.Approve(F.S1, F.C, at: T0.AddHours(2.5));
        instance.Approve(F.S1, F.D, at: T0.AddHours(2.5));
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        instance.View(F.S2).Approvers.ShouldBeEmpty();
    }

    [Fact]
    public void SomeoneAnEscalatorAddedIsNotCarriedOnward()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]), F.Stage(F.S2, 2, QuorumMode.All, candidates: [F.A, F.B]));
        instance.Escalate(F.S1, [F.A], F.E, "r", instance.Version, T0);
        instance.Approve(F.S1, F.A);
        instance.View(F.S2).Approvers.ShouldBeEmpty();
    }

    [Fact]
    public void ADecisionIdEqualToTheNextLevelsIdCarriesNothing()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.A, F.B]));
        instance.Decide(new ApprovalDecision(F.S2, F.S1, F.A, null, true, null, instance.Version), T0);
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        instance.View(F.S2).Approvers.ShouldBeEmpty();
    }

    [Fact]
    public void AParallelPreviousStepCarriesFromAnyOfItsStages()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.B]), F.Stage(F.S3, 2, QuorumMode.All, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.A);
        instance.Approve(F.S2, F.B);
        instance.State.ShouldBe(ApprovalState.Approved);
        instance.Decisions.Count(d => d.StageId == F.S3 && d.Outcome == RecordedOutcome.ApprovedAutomatically).ShouldBe(2);
    }

    [Fact]
    public void SomeoneWhoApprovedTwoParallelLevelsIsCarriedOnce()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.A]), F.Stage(F.S3, 2, QuorumMode.MinimumN, 2, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.A);
        instance.Approve(F.S2, F.A);
        instance.Decisions.Count(d => d.StageId == F.S3).ShouldBe(1);
        instance.ProvisioningAuthorized.ShouldBeFalse();
        instance.Approve(F.S3, F.B);
        instance.ProvisioningAuthorized.ShouldBeTrue();
    }

    [Fact]
    public void IfACarriedIdWouldClashNothingIsCarried()
    {
        // A caller chooses decision ids, so one can collide with the id a carried approval would get. Then nothing is
        // carried, and the people decide as usual.
        var first = F.Id(61);
        var clashing = Xor(first, F.S3);
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 1, candidates: [F.B]), F.Stage(F.S3, 2, candidates: [F.A, F.B]));
        instance.Decide(new ApprovalDecision(first, F.S1, F.A, null, true, null, instance.Version), T0);
        instance.Decide(new ApprovalDecision(clashing, F.S2, F.B, null, true, null, instance.Version), T0);

        instance.View(F.S3).State.ShouldBe(StageState.Active);
        instance.View(F.S3).Approvers.ShouldBeEmpty();
        instance.Decisions.Select(d => d.DecisionId).Distinct().Count().ShouldBe(instance.Decisions.Count);
    }

    private static Guid Xor(Guid x, Guid y)
    {
        var a = x.ToByteArray();
        var b = y.ToByteArray();
        for (var i = 0; i < 16; i++)
        {
            a[i] ^= b[i];
        }

        return new Guid(a);
    }

    [Fact]
    public void AnEscalatedLevelIsNotApprovedAutomatically()
    {
        // Carrying happens only when a level opens, not when an escalation reopens it, even for someone who just approved
        // the level before.
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.Requester]));
        instance.Approve(F.S1, F.A);
        instance.State.ShouldBe(ApprovalState.Held);
        instance.Escalate(F.S2, [F.A], F.E, "r", instance.Version, T0.AddHours(1));
        instance.View(F.S2).State.ShouldBe(StageState.Active);
        instance.View(F.S2).Approvers.ShouldBeEmpty();
    }

    [Fact]
    public void AnIndependentSiblingInTheNextStepStillHoldsBecauseOfTheEarlierDecision()
    {
        // The carry to S2 isn't what holds S3: A's own decision on S1 already makes A ineligible on any independent level.
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.A]), F.Stage(F.S3, 2, QuorumMode.All, independent: true, candidates: [F.A, F.B]));
        instance.Approve(F.S1, F.A);
        instance.View(F.S2).State.ShouldBe(StageState.Approved);
        instance.View(F.S3).State.ShouldBe(StageState.Held);
        instance.ProvisioningAuthorized.ShouldBeFalse();
    }

    [Fact]
    public void TheSameActionsGiveTheSameCarriedDecisions()
    {
        ApprovalInstance Run()
        {
            var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.A]), F.Stage(F.S2, 2, candidates: [F.A]));
            instance.Decide(new ApprovalDecision(F.Id(61), F.S1, F.A, null, true, null, instance.Version), T0);
            return instance;
        }

        Run().Decisions.ShouldBe(Run().Decisions);
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
        var returned = 0;
        var authorizedBeforeApproved = 0;
        var carried = 0;
        var completed = 0;
        var held = 0;
        var escalated = 0;
        var overridden = 0;
        var approvedAfterEscalation = 0;
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
            CheckInvariants(instance, definitions);
            var everHeld = false;
            var everAuthorizedEarly = false;
            var at = F.T0;
            var decisionNumber = 0;

            for (var step = 0; step < 40 && instance.State is ApprovalState.InProgress or ApprovalState.Held; step++)
            {
                var before = Snapshot(instance);
                var versionBefore = instance.Version;
                at = at.AddMinutes(random.Next(0, 90));
                var when = random.Next(5) == 0 ? at.ToOffset(TimeSpan.FromHours(random.Next(-11, 12))) : at;
                try
                {
                    Act(random, instance, routeStages, when, ref decisionNumber);
                }
                catch (ApprovalRefusedException refusal)
                {
                    refusals[refusal.Refusal!.Value] = refusals.GetValueOrDefault(refusal.Refusal!.Value) + 1;

                    // A refusal adds nothing to the history.
                    instance.Version.ShouldBe(versionBefore);
                }

                everHeld |= instance.State == ApprovalState.Held;
                everAuthorizedEarly |= instance.ProvisioningAuthorized && instance.State != ApprovalState.Approved;

                // If the version didn't move, refused or not, nothing else may have either, and there's nothing new to check.
                if (instance.Version == versionBefore)
                {
                    Snapshot(instance).ShouldBe(before);
                }
                else
                {
                    CheckInvariants(instance, definitions);
                }
            }

            held += everHeld ? 1 : 0;
            authorizedBeforeApproved += everAuthorizedEarly ? 1 : 0;

            escalated += instance.Escalations.Count;
            approved += instance.State == ApprovalState.Approved ? 1 : 0;
            rejected += instance.State == ApprovalState.Rejected ? 1 : 0;
            cancelled += instance.State == ApprovalState.Cancelled ? 1 : 0;
            withdrawn += instance.State == ApprovalState.Withdrawn ? 1 : 0;
            returned += instance.State == ApprovalState.Returned ? 1 : 0;
            carried += instance.Decisions.Count(d => d.Outcome == RecordedOutcome.ApprovedAutomatically);
            completed += instance.Stages.Count(v => v.State == StageState.Completed);
            approvedAfterEscalation += instance.State == ApprovalState.Approved && instance.Escalations.Count > 0 ? 1 : 0;
        }

        // The generator must really reach every outcome and rule, or this proves nothing.
        approved.ShouldBeGreaterThan(100);
        rejected.ShouldBeGreaterThan(100);
        cancelled.ShouldBeGreaterThan(100);
        withdrawn.ShouldBeGreaterThan(100);
        returned.ShouldBeGreaterThan(25);
        authorizedBeforeApproved.ShouldBeGreaterThan(10);
        carried.ShouldBeGreaterThan(15);
        completed.ShouldBeGreaterThan(50);
        held.ShouldBeGreaterThan(100);
        escalated.ShouldBeGreaterThan(100);
        overridden.ShouldBeGreaterThan(100);
        approvedAfterEscalation.ShouldBeGreaterThan(0);
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
            ApprovalRefusal.WrongActionForStage,
        })
        {
            refusals.GetValueOrDefault(rule).ShouldBeGreaterThan(0, rule.ToString());
        }
    }

    [Fact]
    public void EscalatedAllStagesStillNeedEveryoneOnTheLastList()
    {
        // The general generator rarely gets an ALL stage through the escalation rules, so this one aims straight at it.
        var random = new Random(31);
        Guid[] pool = [F.A, F.B, F.C, F.D, F.Id(20), F.Id(21), F.Id(22), F.Requester];
        var approvedAfterEscalation = 0;
        for (var round = 0; round < 1_500; round++)
        {
            var configured = pool.Take(7).OrderBy(_ => random.Next()).Take(random.Next(2, 4)).ToArray();
            var stage = new StageDefinition(F.S1, 1, QuorumMode.All, null, false, false, true, TimeSpan.FromHours(1), TimeoutAction.Escalate, configured);
            var instance = ApprovalInstance.Bind(F.Binding, ApprovalRoute.Create([stage]), F.T0);
            var at = F.T0;
            Guid[]? lastAccepted = null;
            for (var step = 0; step < 30 && instance.State is ApprovalState.InProgress or ApprovalState.Held; step++)
            {
                at = at.AddMinutes(random.Next(10, 90));
                try
                {
                    switch (random.Next(3))
                    {
                        case 0:
                            instance.ApplyTimeouts(at, instance.Version);
                            break;
                        case 1:
                            Guid[] list2 = [.. pool.OrderBy(_ => random.Next()).Take(random.Next(1, 5))];
                            instance.Escalate(F.S1, list2, F.Id(800 + step), "r", instance.Version, at);
                            lastAccepted = list2;
                            break;
                        default:
                            var list = instance.View(F.S1).Candidates;
                            instance.Approve(F.S1, list[random.Next(list.Count)], at: at);
                            break;
                    }
                }
                catch (ApprovalRefusedException)
                {
                    // Most attempts are refused. What matters is what happens when one isn't.
                }
            }

            if (instance.State != ApprovalState.Approved || instance.Escalations.Count == 0)
            {
                continue;
            }

            approvedAfterEscalation++;
            var principals = instance.Decisions.Select(d => d.Principal).ToHashSet();
            var lastList = lastAccepted!.Where(c => c != F.Requester).ToList();

            // Everyone on the last list approved, the list never went below its size at opening, and nobody counted twice.
            lastList.All(principals.Contains).ShouldBeTrue();
            lastList.Count.ShouldBeGreaterThanOrEqualTo(configured.Length);
            instance.Decisions.Count.ShouldBe(principals.Count);

            // A real decision came after the last escalation: it never closed the stage itself.
            var lastEscalation = instance.History.Select(e => e.Kind).ToList().LastIndexOf(ApprovalEventKind.StageEscalated);
            var lastDecision = instance.History.Select(e => e.Kind).ToList().LastIndexOf(ApprovalEventKind.DecisionApproved);
            lastDecision.ShouldBeGreaterThan(lastEscalation);
        }

        approvedAfterEscalation.ShouldBeGreaterThan(10);
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
            // The first is always an applicable decision, or the route could be refused for having no decision.
            stages[i] = RandomStage(random, F.Id(100 + i), mustApply: i == 0);
        }

        return stages;
    }

    private static StageDefinition RandomStage(Random random, Guid id, bool mustApply)
    {
        var kind = !mustApply && random.Next(4) == 0 ? StageKind.Action : StageKind.Decision;
        var mandatory = kind == StageKind.Decision && random.Next(3) == 0;
        var independent = random.Next(3) == 0;
        Guid[] candidates = [.. People.OrderBy(_ => random.Next()).Take(random.Next(1, 5))];
        var quorum = kind == StageKind.Action ? QuorumMode.AnyOne : (QuorumMode)random.Next(1, 4);
        var timeout = (TimeoutAction)random.Next(1, 4);
        return new StageDefinition(
            id,
            random.Next(1, 4),
            quorum,
            quorum == QuorumMode.MinimumN ? random.Next(1, candidates.Length + 1) : null,
            mandatory,
            independent,
            mustApply || mandatory || random.Next(5) != 0,
            TimeSpan.FromHours(random.Next(1, 8)),
            kind == StageKind.Action && timeout == TimeoutAction.Reject ? TimeoutAction.Cancel : timeout,
            candidates,
            kind);
    }

    private static void Act(Random random, ApprovalInstance instance, StageDefinition[] stages, DateTimeOffset at, ref int decisionNumber)
    {
        var chosen = stages[random.Next(stages.Length)];
        var stage = chosen.StageId;

        // Half the time someone on the stage's own list, or most attempts stop at the first check.
        var person = chosen.Candidates.Count > 0 && random.Next(2) == 0
            ? chosen.Candidates[random.Next(chosen.Candidates.Count)]
            : People[random.Next(People.Length)];
        if (chosen.Kind == StageKind.Action && random.Next(2) == 0)
        {
            var forWhom = random.Next(4) == 0 ? chosen.Candidates.Count > 0 ? chosen.Candidates[random.Next(chosen.Candidates.Count)] : F.A : (Guid?)null;
            instance.CompleteAction(new ActionCompletion(F.Id(5000 + decisionNumber++), stage, forWhom is null ? person : F.Id(810), forWhom, null, instance.Version), at);
            return;
        }

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
            case 9:
            case 10:
            case 11:
                instance.CompleteAction(new ActionCompletion(F.Id(5000 + decisionNumber++), stage, person, null, null, instance.Version), at);
                break;
            case 12:
                instance.ReturnForChanges(stage, person, random.Next(4) == 0 ? People[random.Next(People.Length)] : null, "r", instance.Version, at);
                break;
            default:
                instance.Decide(new ApprovalDecision(F.Id(5000 + decisionNumber++), stage, person, null, true, null, instance.Version), at);
                break;
        }
    }

    private static void CheckInvariants(ApprovalInstance instance, Dictionary<Guid, StageDefinition> definitions)
    {
        var excluded = new[] { F.Requester, F.Beneficiary, F.PolicyMaker };
        var views = instance.Stages;
        var viewOf = views.ToDictionary(v => v.StageId);

        // Plain comparisons below, not ShouldAllBe or typed ShouldBe: this runs tens of thousands of times, and those are slow.
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
        // Ordered by the history, not by time: several things can happen at one instant.
        var decisionsSoFar = 0;
        var escalationsSoFar = 0;
        foreach (var entry in instance.History)
        {
            if (entry.Kind is ApprovalEventKind.DecisionApproved or ApprovalEventKind.DecisionRejected or ApprovalEventKind.ActionCompleted or ApprovalEventKind.DecisionApprovedAutomatically)
            {
                decisionsSoFar++;
            }
            else if (entry.Kind == ApprovalEventKind.StageEscalated)
            {
                var escalation = instance.Escalations[escalationsSoFar++];
                var deciders2 = instance.Decisions.Take(decisionsSoFar).Where(d => d.StageId == escalation.StageId).SelectMany(d => new[] { d.Actor, d.Principal });
                deciders2.Any(p => escalation.NewCandidates.Contains(p) && !escalation.PreviousCandidates.Contains(p)).ShouldBeFalse();
            }
        }

        // What each entry says it was matches the kind of level it is on.
        instance.Decisions.All(d => (definitions[d.StageId].Kind == StageKind.Action) == (d.Outcome == RecordedOutcome.Completed)).ShouldBeTrue();

        // A stage closes as approved only after an approving decision on that same stage since it last opened, so neither an
        // escalation (which reopens it) nor anything else can close it alone. Parallel stages may close side by side.
        for (var i = 0; i < instance.History.Count; i++)
        {
            if (instance.History[i].Kind == ApprovalEventKind.StageApproved)
            {
                var stageId = instance.History[i].StageId;
                var opened = instance.History.Take(i).ToList().FindLastIndex(e => e.Kind is ApprovalEventKind.StageActivated or ApprovalEventKind.StageEscalated && e.StageId == stageId);
                opened.ShouldBeGreaterThanOrEqualTo(0);
                instance.History.Skip(opened).Take(i - opened).Any(e => e.StageId == stageId && e.Kind is ApprovalEventKind.DecisionApproved or ApprovalEventKind.DecisionApprovedAutomatically).ShouldBeTrue();
                (definitions[stageId!.Value].Kind == StageKind.Decision).ShouldBeTrue();
            }

            // A task is done only straight after someone finished it, on an action level.
            if (instance.History[i].Kind == ApprovalEventKind.StageCompleted)
            {
                var previous = i - 1;
                while (instance.History[previous].Kind == ApprovalEventKind.StageHeld)
                {
                    previous--;
                }

                (instance.History[previous].Kind == ApprovalEventKind.ActionCompleted).ShouldBeTrue();
                (instance.History[previous].StageId == instance.History[i].StageId).ShouldBeTrue();
                (definitions[instance.History[i].StageId!.Value].Kind == StageKind.Action).ShouldBeTrue();
            }
        }

        // Authorized exactly when every applicable decision level was decided, worked out here from the route and the views.
        var decisionViews = definitions.Values.Where(d => d.Kind == StageKind.Decision).Select(d => viewOf[d.StageId]).ToList();
        var allDecided = decisionViews.All(v => v.State is StageState.Approved or StageState.NotApplicable) && decisionViews.Any(v => v.State == StageState.Approved);
        var stillValid = instance.State is ApprovalState.InProgress or ApprovalState.Held or ApprovalState.Approved;
        instance.ProvisioningAuthorized.ShouldBe(allDecided && stillValid);

        // Every approved decision level has a real approval behind it, and action levels never count as one.
        foreach (var approved in decisionViews.Where(v => v.State == StageState.Approved))
        {
            instance.Decisions.Any(d => d.StageId == approved.StageId && d.Outcome is RecordedOutcome.Approved or RecordedOutcome.ApprovedAutomatically).ShouldBeTrue();
        }

        // A carried approval is only ever on an ordinary decision level, for the same person, from their own real decision on
        // the previous applicable decision step, which was never escalated, with no applicable task in that step or between,
        // and only when the level first opened. Worked out here from the route and the history.
        var byId = instance.Decisions.ToDictionary(d => d.DecisionId);
        foreach (var auto in instance.Decisions.Where(d => d.Outcome == RecordedOutcome.ApprovedAutomatically))
        {
            var here = definitions[auto.StageId];
            (here.Kind == StageKind.Decision && !here.Mandatory && !here.Independent).ShouldBeTrue();
            var source = byId[auto.CarriedFrom!.Value];
            (source.Principal == auto.Principal && source.Actor == source.Principal && auto.Actor == auto.Principal).ShouldBeTrue();
            (source.Outcome == RecordedOutcome.Approved).ShouldBeTrue();
            instance.Escalations.Any(e => e.StageId == source.StageId).ShouldBeFalse();
            var previousStep = definitions.Values.Where(d => d.Kind == StageKind.Decision && d.Applicable && d.Sequence < here.Sequence).Select(d => d.Sequence).DefaultIfEmpty(0).Max();
            (definitions[source.StageId].Sequence == previousStep).ShouldBeTrue();
            definitions.Values.Any(d => d.Kind == StageKind.Action && d.Applicable && d.Sequence >= previousStep && d.Sequence < here.Sequence).ShouldBeFalse();
            instance.History.Any(e => e.Kind == ApprovalEventKind.StageEscalated && e.StageId == auto.StageId).ShouldBeFalse();
        }

        // Whenever provisioning is authorized, at least one approval was a real decision, not an automatic one.
        if (instance.ProvisioningAuthorized)
        {
            instance.Decisions.Any(d => d.Outcome == RecordedOutcome.Approved).ShouldBeTrue();
        }

        // Every counted person was on the stage's list at some point.
        foreach (var decision in instance.Decisions)
        {
            var everListed = definitions[decision.StageId].Candidates.Concat(instance.Escalations.Where(e => e.StageId == decision.StageId).SelectMany(e => e.NewCandidates));
            everListed.Contains(decision.Principal).ShouldBeTrue();
        }

        // Steps run in order: nothing is open while an earlier applicable stage is unfinished.
        foreach (var open in views.Where(s => s.State is StageState.Active or StageState.Held))
        {
            views.Any(s => s.Sequence < open.Sequence && s.State is not (StageState.Approved or StageState.NotApplicable or StageState.Completed)).ShouldBeFalse();
        }

        // An active stage always has someone left who can act.
        foreach (var active in views.Where(s => s.State == StageState.Active))
        {
            var decided = instance.Decisions.Where(d => d.StageId == active.StageId).SelectMany(d => new[] { d.Actor, d.Principal }).ToHashSet();
            active.Eligible.Any(c => !decided.Contains(c)).ShouldBeTrue();
        }

        // Whenever provisioning is authorized, count every decision level again from the decisions alone, without using the
        // state machine's own quorum code. This is the moment that matters, and it comes before Approved.
        if (instance.ProvisioningAuthorized)
        {
            foreach (var stage in definitions.Values.Where(d => d.Kind == StageKind.Decision && d.Applicable))
            {
                (viewOf[stage.StageId].State == StageState.Approved).ShouldBeTrue();
                var principals = instance.Decisions.Where(d => d.StageId == stage.StageId && d.Outcome is RecordedOutcome.Approved or RecordedOutcome.ApprovedAutomatically).Select(d => d.Principal).ToHashSet();
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
            }
        }

        if (instance.State != ApprovalState.Approved)
        {
            return;
        }

        // Approved means authorized, on at least one real approval, and every task finished.
        instance.ProvisioningAuthorized.ShouldBeTrue();
        decisionViews.Any(v => v.State == StageState.Approved).ShouldBeTrue();
        instance.Decisions.All(d => d.Outcome != RecordedOutcome.Rejected).ShouldBeTrue();
        foreach (var task in definitions.Values.Where(d => d.Kind == StageKind.Action))
        {
            var state = viewOf[task.StageId].State;
            (task.Applicable ? state == StageState.Completed : state == StageState.NotApplicable).ShouldBeTrue();
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
