using IAM.BuildingBlocks.Abstractions.People;
using IAM.Modules.Catalog.Contracts;
using IAM.Modules.Organization.Contracts;
using IAM.Modules.Workflow.Application.Approvers;
using IAM.Modules.Workflow.Domain.Approvals;
using Shouldly;

namespace IAM.Workflow.Tests;

/// <summary>Regression tests for the stage 4 security and code review findings (DL-071).</summary>
public sealed class StageFourReviewTests
{
    private static readonly DateTimeOffset T0 = F.T0;
    private static readonly Guid App = F.Id(700);
    private static readonly Guid Role = F.Id(701);
    private static readonly string Hash = new('c', 64);
    private static readonly HashSet<PersonId> Excluded = [F.Requester, F.Beneficiary, F.PolicyMaker];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StageDefinition Level(Guid id, int sequence, ApproverSource source, TimeoutAction onTimeout = TimeoutAction.Escalate) =>
        new(id, sequence, QuorumMode.AnyOne, null, false, false, true, TimeSpan.FromHours(4), onTimeout, source);

    private static ApprovalInstance Bound(params StageDefinition[] stages) => ApprovalInstance.Bind(F.Binding, ApprovalRoute.Create(stages), T0);

    private static ApproverResolver Resolver(Directory directory) => new(directory, directory, new ApproverResolutionSettings());

    // ---- A required role with nobody active holds (DL-071a) ----

    [Fact]
    public async Task ARequiredRoleHeldByAWorkgroupWithNoActiveListHolds()
    {
        var directory = new Directory();
        directory.Application(F.C.Value, new ApplicationRoleHolding(Role, ApplicationRoleRequirement.Required, ApplicationRoleHolderKind.Group, "grp_draft_only"));
        directory.Workgroup("iam_managers", F.E);
        var resolution = await Resolver(directory).ResolveAsync(ApproverSource.ApplicationRole(App, Role), Excluded, Ct);
        resolution.Outcome.ShouldBe(ResolutionOutcome.RequiredRoleEmpty);
        resolution.People.ShouldBeEmpty();
        resolution.Steps.Select(s => (s.Source, s.Result)).ShouldBe([(ApproverSourceKind.ApplicationRole, ResolutionStepResult.NotFound), (ApproverSourceKind.Workgroup, ResolutionStepResult.NotFound)]);
    }

    [Fact]
    public async Task ARequiredRoleWithADamagedHolderHolds()
    {
        var directory = new Directory();
        directory.Application(F.C.Value, new ApplicationRoleHolding(Role, ApplicationRoleRequirement.Required, ApplicationRoleHolderKind.Person, " damaged"));
        (await Resolver(directory).ResolveAsync(ApproverSource.ApplicationRole(App, Role), Excluded, Ct)).Outcome.ShouldBe(ResolutionOutcome.RequiredRoleEmpty);

        directory.Application(F.C.Value, new ApplicationRoleHolding(Role, ApplicationRoleRequirement.Required, ApplicationRoleHolderKind.Group, "grp_damaged"));
        directory.Workgroups["grp_damaged"] = new WorkgroupMembership("grp_damaged", 1, new string('d', 64), [F.A.Value, " bad"]);
        (await Resolver(directory).ResolveAsync(ApproverSource.ApplicationRole(App, Role), Excluded, Ct)).Outcome.ShouldBe(ResolutionOutcome.RequiredRoleEmpty);
    }

    [Fact]
    public async Task AnOptionalRoleWithNobodyActiveStillGoesToTheOwner()
    {
        var directory = new Directory();
        directory.Application(F.C.Value, new ApplicationRoleHolding(Role, ApplicationRoleRequirement.Optional, ApplicationRoleHolderKind.Group, "grp_draft_only"));
        var resolution = await Resolver(directory).ResolveAsync(ApproverSource.ApplicationRole(App, Role), Excluded, Ct);
        resolution.People.ShouldBe([F.C]);

        var instance = Bound(Level(F.S1, 1, ApproverSource.ApplicationRole(App, Role)));
        Should.NotThrow(() => instance.SupplyApprovers(F.S1, resolution, instance.Version, T0));
    }

    // ---- Two escalators for a level that held as blocked (DL-071b) ----

    [Theory]
    [InlineData(ResolutionOutcome.RequiredRoleEmpty)]
    [InlineData(ResolutionOutcome.SourceUnavailable)]
    public void ALevelHeldAsBlockedTakesTwoEscalators(ResolutionOutcome outcome)
    {
        var source = outcome == ResolutionOutcome.RequiredRoleEmpty ? ApproverSource.ApplicationRole(App, Role) : ApproverSource.Unbuilt(ApproverSourceKind.Manager);
        var reference = outcome == ResolutionOutcome.RequiredRoleEmpty ? $"{App:D}:{Role:D}" : null;
        var result = outcome == ResolutionOutcome.RequiredRoleEmpty ? ResolutionStepResult.Empty : ResolutionStepResult.Unavailable;
        var instance = Bound(Level(F.S1, 1, source));
        instance.SupplyApprovers(F.S1, new ApproverResolution(outcome, [], [new ResolutionStep(source.Kind, result, reference, null, null, 0, 0)]), instance.Version, T0);
        instance.View(F.S1).State.ShouldBe(StageState.Held);

        F.Refusal(() => instance.Escalate(F.S1, [F.A], F.E, "r", instance.Version, T0)).ShouldBe(ApprovalRefusal.EscalationNeedsSecondPerson);
        instance.EscalateWithSecond(F.S1, [F.A], F.E, T0);
        instance.Escalations.Single().SecondActor.ShouldBe(F.SecondEscalator);
    }

    [Fact]
    public void ALevelThatFoundNobodyKeepsOneEscalator()
    {
        var instance = Bound(Level(F.S1, 1, ApproverSource.Workgroup("grp_a")));
        instance.SupplyApprovers(F.S1, new ApproverResolution(ResolutionOutcome.NobodyAvailable, [], [new ResolutionStep(ApproverSourceKind.Workgroup, ResolutionStepResult.Empty, "grp_a", 1, null, 0, 0)]), instance.Version, T0);
        instance.Escalate(F.S1, [F.A], F.E, "r", instance.Version, T0);
        instance.View(F.S1).State.ShouldBe(StageState.Active);
    }

    // ---- Withdrawal and the second escalator's reason (DL-071c) ----

    [Fact]
    public void SomeoneWithAConflictCantWithdrawAProposal()
    {
        var instance = F.Start(F.Stage(F.S1, 1, mandatory: true, candidates: [F.Requester]), F.Stage(F.S2, 1, candidates: [F.C]));
        instance.ProposeEscalation(F.S1, [F.A], F.E, "r", instance.Version, T0);
        F.Refusal(() => instance.WithdrawEscalation(F.S1, F.C, "No", instance.Version, T0)).ShouldBe(ApprovalRefusal.EscalatorConflict);
        F.Refusal(() => instance.WithdrawEscalation(F.S1, F.A, "No", instance.Version, T0)).ShouldBe(ApprovalRefusal.EscalatorConflict);
        F.Refusal(() => instance.WithdrawEscalation(F.S1, F.SecondEscalator, null, instance.Version, T0)).ShouldBe(ApprovalRefusal.InvalidReason);
        instance.View(F.S1).PendingEscalation.ShouldNotBeNull();

        instance.WithdrawEscalation(F.S1, F.SecondEscalator, "Wrong people", instance.Version, T0);
        var withdrawal = instance.EscalationWithdrawals.Single();
        withdrawal.Actor.ShouldBe(F.SecondEscalator);
        withdrawal.Reason.ShouldBe("Wrong people");
        withdrawal.ToString().ShouldNotContain("Wrong");
    }

    [Fact]
    public void TheProposerMayWithdrawTheirOwnProposal()
    {
        var instance = F.Start(F.Stage(F.S1, 1, independent: true, candidates: [F.Requester]));
        instance.ProposeEscalation(F.S1, [F.A], F.E, "r", instance.Version, T0);
        instance.WithdrawEscalation(F.S1, F.E, "Changed my mind", instance.Version, T0);
        instance.View(F.S1).PendingEscalation.ShouldBeNull();
    }

    [Fact]
    public void TheSecondEscalatorGivesAReasonAndItIsRecorded()
    {
        var instance = F.Start(F.Stage(F.S1, 1, mandatory: true, candidates: [F.Requester]));
        instance.ProposeEscalation(F.S1, [F.A], F.E, "Owner away", instance.Version, T0);
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.A], F.SecondEscalator, null, instance.Version, T0)).ShouldBe(ApprovalRefusal.InvalidReason);
        F.Refusal(() => instance.ApproveEscalation(F.S1, [F.A], F.SecondEscalator, " ", instance.Version, T0)).ShouldBe(ApprovalRefusal.InvalidReason);
        instance.ApproveEscalation(F.S1, [F.A], F.SecondEscalator, "Checked with the owner", instance.Version, T0);
        instance.Escalations.Single().SecondReason.ShouldBe("Checked with the owner");
        instance.Escalations.Single().ToString().ShouldNotContain("Checked");
    }

    // ---- A resolution's people must fit the level (DL-071e) ----

    [Fact]
    public void ANamedLevelTakesOnlyItsOwnPeopleUnlessItFellBack()
    {
        var instance = Bound(Level(F.S1, 1, ApproverSource.Named(F.A, F.B)));
        var version = instance.Version;
        F.Refusal(() => instance.SupplyApprovers(F.S1, new ApproverResolution(ResolutionOutcome.Resolved, [F.C], [new ResolutionStep(ApproverSourceKind.NamedUsers, ResolutionStepResult.Found, null, null, null, 1, 0)]), version, T0))
            .ShouldBe(ApprovalRefusal.InvalidResolution);

        // Falling back to the fallback workgroup gives its members instead.
        var fellBack = new ApproverResolution(ResolutionOutcome.Resolved, [F.E], [
            new ResolutionStep(ApproverSourceKind.NamedUsers, ResolutionStepResult.AllExcluded, null, null, null, 2, 2),
            new ResolutionStep(ApproverSourceKind.FallbackWorkgroup, ResolutionStepResult.Found, "iam_managers", 1, null, 1, 0)]);
        F.Refusal(() => instance.SupplyApprovers(F.S1, fellBack with { Steps = [fellBack.Steps[1]] }, version, T0)).ShouldBe(ApprovalRefusal.InvalidResolution);
        instance.SupplyApprovers(F.S1, fellBack, version, T0);
        instance.View(F.S1).Candidates.ShouldBe([F.E]);
    }

    [Fact]
    public void AResolutionMustReadWhereTheLevelPointsAndCountWhoItFound()
    {
        var instance = Bound(Level(F.S1, 1, ApproverSource.Workgroup("grp_a")));
        var version = instance.Version;
        ApproverResolution Of(string? reference, int found, int excluded, params PersonId[] people) =>
            new(ResolutionOutcome.Resolved, people, [new ResolutionStep(ApproverSourceKind.Workgroup, ResolutionStepResult.Found, reference, 1, null, found, excluded)]);

        F.Refusal(() => instance.SupplyApprovers(F.S1, Of("grp_other", 1, 0, F.A), version, T0)).ShouldBe(ApprovalRefusal.InvalidResolution);
        F.Refusal(() => instance.SupplyApprovers(F.S1, Of(null, 1, 0, F.A), version, T0)).ShouldBe(ApprovalRefusal.InvalidResolution);
        F.Refusal(() => instance.SupplyApprovers(F.S1, Of("grp_a", 3, 0, F.A), version, T0)).ShouldBe(ApprovalRefusal.InvalidResolution);
        F.Refusal(() => instance.SupplyApprovers(F.S1, Of("grp_a", 2, 1, F.A, F.B), version, T0)).ShouldBe(ApprovalRefusal.InvalidResolution);
        F.Refusal(() => instance.SupplyApprovers(F.S1, Of("grp_a", 1, 0, F.A) with { Steps = [new ResolutionStep(ApproverSourceKind.Workgroup, ResolutionStepResult.Empty, "grp_a", 1, null, 0, 0)] }, version, T0))
            .ShouldBe(ApprovalRefusal.InvalidResolution);
        instance.SupplyApprovers(F.S1, Of("grp_a", 3, 1, F.A, F.B), version, T0);
    }

    // ---- Engine state fixes (DL-071d) ----

    [Fact]
    public void NothingIsCarriedWhileTheInstanceIsHeld()
    {
        // Step 2 has two levels: S3's resolution fails and holds the request, then S2 gets the step-1 approver.
        var instance = Bound(
            Level(F.S1, 1, ApproverSource.Named(F.A)),
            Level(F.S2, 2, ApproverSource.Workgroup("grp_a")),
            Level(F.S3, 2, ApproverSource.Workgroup("grp_a")));
        instance.SupplyApprovers(F.S1, new ApproverResolution(ResolutionOutcome.Resolved, [F.A], [new ResolutionStep(ApproverSourceKind.NamedUsers, ResolutionStepResult.Found, null, null, null, 1, 0)]), instance.Version, T0);
        instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, null, instance.Version), T0);
        instance.SupplyApprovers(F.S3, new ApproverResolution(ResolutionOutcome.NobodyAvailable, [], [new ResolutionStep(ApproverSourceKind.Workgroup, ResolutionStepResult.Empty, "grp_a", 1, null, 0, 0)]), instance.Version, T0);
        instance.State.ShouldBe(ApprovalState.Held);

        instance.SupplyApprovers(F.S2, new ApproverResolution(ResolutionOutcome.Resolved, [F.A, F.B], [new ResolutionStep(ApproverSourceKind.Workgroup, ResolutionStepResult.Found, "grp_a", 1, null, 2, 0)]), instance.Version, T0);
        instance.Decisions.ShouldNotContain(d => d.Outcome == RecordedOutcome.ApprovedAutomatically);
        instance.View(F.S2).State.ShouldBe(StageState.Active);
    }

    [Fact]
    public void ALevelHeldByItsResolutionRecordsItsTimeoutOnce()
    {
        var instance = Bound(Level(F.S1, 1, ApproverSource.Workgroup("grp_a")), Level(F.S2, 1, ApproverSource.Named(F.A), TimeoutAction.Reject));
        instance.SupplyApprovers(F.S1, new ApproverResolution(ResolutionOutcome.NobodyAvailable, [], [new ResolutionStep(ApproverSourceKind.Workgroup, ResolutionStepResult.Empty, "grp_a", 1, null, 0, 0)]), instance.Version, T0);
        instance.SupplyApprovers(F.S2, new ApproverResolution(ResolutionOutcome.Resolved, [F.A], [new ResolutionStep(ApproverSourceKind.NamedUsers, ResolutionStepResult.Found, null, null, null, 1, 0)]), instance.Version, T0);

        instance.ApplyTimeouts(T0.AddHours(4), instance.Version);
        instance.State.ShouldBe(ApprovalState.Rejected);
        instance.History.Count(e => e.Kind == ApprovalEventKind.StageTimedOut && e.StageId == F.S1).ShouldBe(1);
        instance.History.Count(e => e.Kind == ApprovalEventKind.StageTimedOut && e.StageId == F.S2).ShouldBe(1);

        var escalating = Bound(Level(F.S1, 1, ApproverSource.Workgroup("grp_a")));
        escalating.SupplyApprovers(F.S1, new ApproverResolution(ResolutionOutcome.NobodyAvailable, [], [new ResolutionStep(ApproverSourceKind.Workgroup, ResolutionStepResult.Empty, "grp_a", 1, null, 0, 0)]), escalating.Version, T0);
        escalating.ApplyTimeouts(T0.AddHours(4), escalating.Version);
        escalating.ApplyTimeouts(T0.AddHours(5), escalating.Version);
        escalating.History.Count(e => e.Kind == ApprovalEventKind.StageTimedOut).ShouldBe(1);
        escalating.View(F.S1).State.ShouldBe(StageState.Held);
    }

    [Fact]
    public void AWaitingProposalLapsesWhenTheRequestCloses()
    {
        var instance = F.Start(F.Stage(F.S1, 1, mandatory: true, candidates: [F.Requester]));
        instance.ProposeEscalation(F.S1, [F.A], F.E, "r", instance.Version, T0);
        instance.Withdraw(F.Requester, null, instance.Version, T0);
        instance.View(F.S1).PendingEscalation.ShouldBeNull();
    }

    [Fact]
    public void ProposalsDontCountTowardsTheEscalationLimit()
    {
        var instance = F.Start(F.Stage(F.S1, 1, mandatory: true, candidates: [F.Requester]));
        for (var i = 0; i < ApprovalLimits.MaxEscalationsPerStage + 2; i++)
        {
            instance.ProposeEscalation(F.S1, [F.A], F.P(700 + i), "r", instance.Version, T0);
        }

        instance.View(F.S1).Escalations.ShouldBe(0);
        instance.ApproveEscalation(F.S1, [F.A], F.SecondEscalator, "Checked", instance.Version, T0);
        instance.View(F.S1).Escalations.ShouldBe(1);
    }

    private sealed class Directory : IApplicationApproverDirectory, IWorkgroupDirectory
    {
        public Dictionary<string, WorkgroupMembership> Workgroups { get; } = [];

        private ApplicationApprovers? _application;

        public void Application(string owner, params ApplicationRoleHolding[] roles) => _application = new ApplicationApprovers(App, 3, Hash, owner, roles);

        public void Workgroup(string key, params PersonId[] members) =>
            Workgroups[key] = new WorkgroupMembership(key, 2, new string('d', 64), [.. members.Select(m => m.Value)]);

        public Task<ApplicationApprovers?> FindActiveAsync(Guid applicationId, CancellationToken cancellationToken) =>
            Task.FromResult(applicationId == App ? _application : null);

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Workgroups.ContainsKey(key));

        public Task<WorkgroupMembership?> FindActiveAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Workgroups.GetValueOrDefault(key));
    }
}
