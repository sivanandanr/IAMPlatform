using IAM.BuildingBlocks.Abstractions.People;
using IAM.BuildingBlocks.Security;
using IAM.Modules.Workflow.Domain.Approvals;
using IAM.Modules.Workflow.Infrastructure.Approvers;
using Shouldly;

namespace IAM.Workflow.Tests;

/// <summary>Who may choose a held level's approvers (DL-048, DL-057a, DL-070).</summary>
public sealed class TwoPersonEscalationTests
{
    private static readonly DateTimeOffset T0 = F.T0;
    private static readonly PersonId Proposer = F.E;
    private static readonly PersonId Second = F.SecondEscalator;

    /// <summary>A stage whose only person is excluded, so it holds as soon as it opens.</summary>
    private static ApprovalInstance Held(bool mandatory = false, bool independent = false, TimeoutAction onTimeout = TimeoutAction.Escalate) =>
        F.Start(F.Stage(F.S1, 1, mandatory: mandatory, independent: independent, onTimeout: onTimeout, deadline: TimeSpan.FromHours(4), candidates: [F.Requester]));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AMandatoryOrIndependentLevelCantBeEscalatedByOnePerson(bool mandatory, bool independent)
    {
        var instance = Held(mandatory, independent);
        var version = instance.Version;
        F.Refusal(() => instance.Escalate(F.S1, [F.A], Proposer, "r", version, T0)).ShouldBe(ApprovalRefusal.EscalationNeedsSecondPerson);
        instance.Version.ShouldBe(version);
        instance.Escalations.ShouldBeEmpty();
    }

    [Fact]
    public void AnOrdinaryLevelHasOneEscalatorAndNothingToPropose()
    {
        var instance = Held();
        F.Refusal(() => instance.ProposeEscalation(F.S1, [F.A], Proposer, "r", instance.Version, T0)).ShouldBe(ApprovalRefusal.SingleEscalatorStage);
        instance.Escalate(F.S1, [F.A], Proposer, "r", instance.Version, T0);
        instance.Escalations.Single().SecondActor.ShouldBeNull();
    }

    [Fact]
    public void AProposalChangesNoApproverUntilASecondEscalatorApprovesTheSameList()
    {
        var instance = Held(mandatory: true);
        instance.ProposeEscalation(F.S1, [F.A, F.B], Proposer, "Owner away", instance.Version, T0);
        instance.History[^1].Kind.ShouldBe(ApprovalEventKind.EscalationProposed);
        var view = instance.View(F.S1);
        view.State.ShouldBe(StageState.Held);
        view.Candidates.ShouldBeEmpty();
        view.PendingEscalation.ShouldNotBeNull().Candidates.ShouldBe([F.A, F.B]);
        F.Refusal(() => instance.Approve(F.S1, F.A)).ShouldBe(ApprovalRefusal.InstanceHeld);

        var version = instance.Version;
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.A, F.B], Proposer, version, T0)).ShouldBe(ApprovalRefusal.ProposerCannotApproveEscalation);
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.A], Second, version, T0)).ShouldBe(ApprovalRefusal.EscalationProposalChanged);
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.B, F.A], Second, version, T0)).ShouldBe(ApprovalRefusal.EscalationProposalChanged);
        F.Refusal(() => instance.ApproveEscalation(F.S1, null!, Second, version, T0)).ShouldBe(ApprovalRefusal.EscalationProposalChanged);
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.A, F.B], F.A, version, T0)).ShouldBe(ApprovalRefusal.EscalatorConflict);
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.A, F.B], F.Beneficiary, version, T0)).ShouldBe(ApprovalRefusal.InvalidActor);
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.A, F.B], default, version, T0)).ShouldBe(ApprovalRefusal.InvalidActor);
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.A, F.B], Second, version - 1, T0)).ShouldBe(ApprovalRefusal.StaleVersion);
        instance.Version.ShouldBe(version);

        instance.ApproveEscalation(F.S1, [F.A, F.B], Second, version, T0);
        instance.View(F.S1).State.ShouldBe(StageState.Active);
        instance.View(F.S1).PendingEscalation.ShouldBeNull();
        var record = instance.Escalations.Single();
        record.Actor.ShouldBe(Proposer);
        record.SecondActor.ShouldBe(Second);
        record.Reason.ShouldBe("Owner away");
        record.NewCandidates.ShouldBe([F.A, F.B]);
        instance.History.Single(e => e.Kind == ApprovalEventKind.StageEscalated).Actor.ShouldBe(Second);

        instance.Approve(F.S1, F.A);
        instance.State.ShouldBe(ApprovalState.Approved);
    }

    [Fact]
    public void NobodyOnAnotherLevelsListChoosesThisOnesApprovers()
    {
        var instance = F.Start(
            F.Stage(F.S1, 1, independent: true, candidates: [F.Requester]),
            F.Stage(F.S2, 2, candidates: [F.C, Second]),
            F.Stage(F.S3, 3, candidates: [F.D, Proposer]));

        // Someone on a later level's list can't choose this one's approvers.
        F.Refusal(() => instance.ProposeEscalation(F.S1, [F.A], Proposer, "r", instance.Version, T0)).ShouldBe(ApprovalRefusal.EscalatorConflict);
        instance.ProposeEscalation(F.S1, [F.A], F.P(77), "r", instance.Version, T0);
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.A], Second, instance.Version, T0)).ShouldBe(ApprovalRefusal.EscalatorConflict);
        instance.ApproveEscalation(F.S1, [F.A], F.P(78), instance.Version, T0);
        instance.View(F.S1).Eligible.ShouldBe([F.A]);
    }

    [Fact]
    public void ANewProposalReplacesAWaitingOneAndAWithdrawnOneCantBeApproved()
    {
        var instance = Held(independent: true);
        instance.ProposeEscalation(F.S1, [F.A], Proposer, "r", instance.Version, T0);
        instance.ProposeEscalation(F.S1, [F.B], F.P(77), "r", instance.Version, T0);
        instance.View(F.S1).PendingEscalation!.Candidates.ShouldBe([F.B]);
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.A], Second, instance.Version, T0)).ShouldBe(ApprovalRefusal.EscalationProposalChanged);

        F.Refusal(() => instance.WithdrawEscalation(F.S1, F.Requester, instance.Version, T0)).ShouldBe(ApprovalRefusal.InvalidActor);
        instance.WithdrawEscalation(F.S1, Second, instance.Version, T0);
        instance.History[^1].Kind.ShouldBe(ApprovalEventKind.EscalationWithdrawn);
        instance.View(F.S1).State.ShouldBe(StageState.Held);
        instance.View(F.S1).PendingEscalation.ShouldBeNull();
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.B], Second, instance.Version, T0)).ShouldBe(ApprovalRefusal.NoEscalationProposed);
        F.Refusal(() => instance.WithdrawEscalation(F.S1, Second, instance.Version, T0)).ShouldBe(ApprovalRefusal.NoEscalationProposed);
    }

    [Fact]
    public void AProposalIsCheckedAgainWhenApproved()
    {
        // A Reject level's deadline passes while the proposal waits: approving it must not undo the rejection.
        var instance = Held(mandatory: true, onTimeout: TimeoutAction.Reject);
        instance.ProposeEscalation(F.S1, [F.A], Proposer, "r", instance.Version, T0.AddHours(1));
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.A], Second, instance.Version, T0.AddHours(4))).ShouldBe(ApprovalRefusal.StagePastDeadline);
        instance.ApplyTimeouts(T0.AddHours(4), instance.Version);
        instance.State.ShouldBe(ApprovalState.Rejected);
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.A], Second, instance.Version, T0.AddHours(4))).ShouldBe(ApprovalRefusal.InstanceClosed);
    }

    [Fact]
    public void ProposalsCountTowardsTheEscalationLimitOnlyWhenApproved()
    {
        var instance = Held(mandatory: true);
        for (var i = 0; i < 3; i++)
        {
            instance.ProposeEscalation(F.S1, [F.A], F.P(700 + i), "r", instance.Version, T0);
        }

        instance.View(F.S1).Escalations.ShouldBe(0);
    }

    [Fact]
    public void WhoeverChoseForAnIndependentLevelChoosesForNoOther()
    {
        var instance = F.Start(F.Stage(F.S1, 1, independent: true, candidates: [F.Requester]), F.Stage(F.S2, 1, candidates: [F.Beneficiary]));
        instance.EscalateWithSecond(F.S1, [F.A], Proposer, T0);
        F.Refusal(() => instance.Escalate(F.S2, [F.B], Proposer, "r", instance.Version, T0)).ShouldBe(ApprovalRefusal.IndependentEscalatorConflict);
        F.Refusal(() => instance.Escalate(F.S2, [F.B], Second, "r", instance.Version, T0)).ShouldBe(ApprovalRefusal.IndependentEscalatorConflict);
        instance.Escalate(F.S2, [F.B], F.P(77), "r", instance.Version, T0);

        // The other way round: someone who chose for another level doesn't choose for an independent one.
        var reverse = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]), F.Stage(F.S2, 1, independent: true, candidates: [F.Beneficiary]));
        reverse.Escalate(F.S1, [F.A], Proposer, "r", reverse.Version, T0);
        F.Refusal(() => reverse.ProposeEscalation(F.S2, [F.B], Proposer, "r", reverse.Version, T0)).ShouldBe(ApprovalRefusal.IndependentEscalatorConflict);
        reverse.ProposeEscalation(F.S2, [F.B], F.P(77), "r", reverse.Version, T0);
        F.Refusal(() => reverse.ApproveEscalation(F.S2, [F.B], Proposer, reverse.Version, T0)).ShouldBe(ApprovalRefusal.IndependentEscalatorConflict);
        reverse.ApproveEscalation(F.S2, [F.B], Second, reverse.Version, T0);
    }

    [Fact]
    public void ProposingCountsAsChoosingEvenIfTheProposalIsWithdrawn()
    {
        // The proposer chose approvers for an independent level, so they choose for no other, and decide nothing on the request.
        var instance = F.Start(
            F.Stage(F.S1, 1, independent: true, candidates: [F.Requester]),
            F.Stage(F.S2, 1, candidates: [F.Beneficiary]));
        instance.ProposeEscalation(F.S1, [F.A], Proposer, "r", instance.Version, T0);
        instance.WithdrawEscalation(F.S1, Proposer, instance.Version, T0);
        F.Refusal(() => instance.Escalate(F.S2, [F.B], Proposer, "r", instance.Version, T0)).ShouldBe(ApprovalRefusal.IndependentEscalatorConflict);

        var later = ApprovalInstance.Bind(
            F.Binding,
            ApprovalRoute.Create([
                F.Stage(F.S1, 1, mandatory: true, candidates: [F.Requester]),
                new StageDefinition(F.S2, 2, QuorumMode.AnyOne, null, false, false, true, TimeSpan.FromHours(4), TimeoutAction.Escalate, ApproverSource.Workgroup("grp_later"))]),
            T0).Supplied();
        later.ProposeEscalation(F.S1, [F.A], Proposer, "r", later.Version, T0);
        later.WithdrawEscalation(F.S1, Proposer, later.Version, T0);
        later.EscalateWithSecond(F.S1, [F.A], F.P(77), T0);
        later.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, null, later.Version), T0);
        later.SupplyApprovers(F.S2, new ApproverResolution(ResolutionOutcome.Resolved, [Proposer, F.B], [new ResolutionStep(ApproverSourceKind.Workgroup, ResolutionStepResult.Found, "grp_later", 1, null, 2, 0)]), later.Version, T0.AddMinutes(1));
        later.View(F.S2).Eligible.ShouldBe([F.B]);
    }

    [Fact]
    public void OrdinaryLevelsMayShareAnEscalator()
    {
        var instance = F.Start(F.Stage(F.S1, 1, candidates: [F.Requester]), F.Stage(F.S2, 1, candidates: [F.Beneficiary]));
        instance.Escalate(F.S1, [F.A], Proposer, "r", instance.Version, T0);
        instance.Escalate(F.S2, [F.B], Proposer, "r", instance.Version, T0);
        instance.Escalations.Count.ShouldBe(2);
    }

    [Fact]
    public void LoggingAProposalNamesNobody()
    {
        var instance = Held(mandatory: true);
        instance.ProposeEscalation(F.S1, [F.A], Proposer, "secret reason", instance.Version, T0);
        var text = instance.View(F.S1).PendingEscalation!.ToString();
        text.ShouldNotContain("person");
        text.ShouldNotContain("secret");
    }
}

/// <summary>The escalate permission is checked server-side for every person who acts (DL-070c).</summary>
public sealed class EscalationPermissionTests
{
    private static readonly DateTimeOffset Now = F.T0.AddHours(1);

    private static ApprovalEscalationService Service(params PersonId[] holders) => new(new Permissions(holders), new FixedClock(Now));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApprovalInstance Held(bool mandatory) =>
        F.Start(F.Stage(F.S1, 1, mandatory: mandatory, candidates: [F.Requester]));

    [Fact]
    public async Task SomeoneWithoutThePermissionChangesNothing()
    {
        var instance = Held(mandatory: false);
        var version = instance.Version;
        (await Should.ThrowAsync<ApprovalRefusedException>(() => Service(F.D).EscalateAsync(instance, F.S1, [F.A], F.E, "r", version, Ct)))
            .Refusal.ShouldBe(ApprovalRefusal.EscalatePermissionMissing);
        (await Should.ThrowAsync<ApprovalRefusedException>(() => Service(F.E).EscalateAsync(instance, F.S1, [F.A], default, "r", version, Ct)))
            .Refusal.ShouldBe(ApprovalRefusal.EscalatePermissionMissing);
        instance.Version.ShouldBe(version);

        await Service(F.E).EscalateAsync(instance, F.S1, [F.A], F.E, "r", version, Ct);
        instance.Escalations.Single().AtUtc.ShouldBe(Now);
    }

    [Fact]
    public async Task BothEscalatorsNeedThePermission()
    {
        var instance = Held(mandatory: true);
        (await Should.ThrowAsync<ApprovalRefusedException>(() => Service().ProposeAsync(instance, F.S1, [F.A], F.E, "r", instance.Version, Ct)))
            .Refusal.ShouldBe(ApprovalRefusal.EscalatePermissionMissing);
        await Service(F.E).ProposeAsync(instance, F.S1, [F.A], F.E, "r", instance.Version, Ct);

        var version = instance.Version;
        (await Should.ThrowAsync<ApprovalRefusedException>(() => Service(F.E).ApproveAsync(instance, F.S1, [F.A], F.SecondEscalator, version, Ct)))
            .Refusal.ShouldBe(ApprovalRefusal.EscalatePermissionMissing);
        (await Should.ThrowAsync<ApprovalRefusedException>(() => Service(F.E).WithdrawAsync(instance, F.S1, F.SecondEscalator, version, Ct)))
            .Refusal.ShouldBe(ApprovalRefusal.EscalatePermissionMissing);
        instance.Version.ShouldBe(version);

        await Service(F.SecondEscalator).ApproveAsync(instance, F.S1, [F.A], F.SecondEscalator, version, Ct);
        instance.View(F.S1).State.ShouldBe(StageState.Active);
    }

    [Fact]
    public async Task ThePermissionCheckedIsTheEscalateAction()
    {
        var permissions = new Permissions(F.E);
        var instance = Held(mandatory: false);
        await new ApprovalEscalationService(permissions, new FixedClock(Now)).EscalateAsync(instance, F.S1, [F.A], F.E, "r", instance.Version, Ct);
        permissions.Asked.ShouldBe([(F.E.Value, "workflow.approvals.escalate")]);
    }

    private sealed class Permissions(params PersonId[] holders) : IPermissionEvaluator
    {
        public List<(string Subject, string Action)> Asked { get; } = [];

        public Task<bool> CanAsync(string subject, string action, CancellationToken cancellationToken)
        {
            Asked.Add((subject, action));
            return Task.FromResult(action == ApprovalEscalationService.Action && holders.Any(h => h.Value == subject));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
