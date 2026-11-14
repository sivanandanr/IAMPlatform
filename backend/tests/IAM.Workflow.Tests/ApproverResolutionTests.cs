using IAM.BuildingBlocks.Abstractions.People;
using IAM.Modules.Catalog.Contracts;
using IAM.Modules.Organization.Contracts;
using IAM.Modules.Workflow.Application.Approvers;
using IAM.Modules.Workflow.Domain.Approvals;
using Shouldly;

namespace IAM.Workflow.Tests;

/// <summary>Levels wait for their approvers when they open, and take them only in a valid resolution (DL-069b).</summary>
public sealed class ApproverSupplyTests
{
    private static readonly DateTimeOffset T0 = F.T0;
    private static readonly Guid App = F.Id(700);
    private static readonly Guid Role = F.Id(701);

    private static ApprovalInstance Bound(params StageDefinition[] stages) => ApprovalInstance.Bind(F.Binding, ApprovalRoute.Create(stages), T0);

    private static StageDefinition Level(Guid id, int sequence, ApproverSource source, QuorumMode quorum = QuorumMode.AnyOne, int? minimum = null, TimeoutAction onTimeout = TimeoutAction.Escalate) =>
        new(id, sequence, quorum, minimum, false, false, true, TimeSpan.FromHours(4), onTimeout, source);

    private static ApproverResolution Resolved(ApproverSourceKind kind, params PersonId[] people) =>
        new(ResolutionOutcome.Resolved, people, [new ResolutionStep(kind, ResolutionStepResult.Found, null, null, null, people.Length, 0)]);

    private static ApproverResolution Failed(ApproverSourceKind kind, ResolutionOutcome outcome, ResolutionStepResult result = ResolutionStepResult.Empty) =>
        new(outcome, [], [new ResolutionStep(kind, result, null, null, null, 0, 0)]);

    [Fact]
    public void AnOpenedLevelWaitsAndNobodyCanActUntilItsApproversAreSupplied()
    {
        var instance = Bound(F.Stage(F.S1, 1, candidates: [F.A]));
        instance.View(F.S1).State.ShouldBe(StageState.AwaitingApprovers);
        instance.View(F.S1).DeadlineUtc.ShouldBe(T0.AddHours(24));
        instance.State.ShouldBe(ApprovalState.InProgress);
        var version = instance.Version;
        F.Refusal(() => instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, null, version), T0)).ShouldBe(ApprovalRefusal.StageNotActive);
        F.Refusal(() => instance.ReturnForChanges(F.S1, F.A, null, "r", version, T0)).ShouldBe(ApprovalRefusal.StageNotActive);
        F.Refusal(() => instance.Escalate(F.S1, [F.B], F.E, "r", version, T0)).ShouldBe(ApprovalRefusal.StageNotEscalatable);
        instance.Version.ShouldBe(version);

        instance.SupplyApprovers(F.S1, Resolved(ApproverSourceKind.NamedUsers, F.A), version, T0);
        instance.View(F.S1).State.ShouldBe(StageState.Active);
        instance.View(F.S1).Candidates.ShouldBe([F.A]);
        instance.View(F.S1).Resolution!.Outcome.ShouldBe(ResolutionOutcome.Resolved);
        instance.Approve(F.S1, F.A);
        instance.State.ShouldBe(ApprovalState.Approved);
    }

    [Fact]
    public void ALaterLevelWaitsOnlyWhenItOpens()
    {
        var instance = Bound(Level(F.S1, 1, ApproverSource.Named(F.A)), Level(F.S2, 2, ApproverSource.ApplicationOwner(App)));
        instance.View(F.S2).State.ShouldBe(StageState.Pending);
        F.Refusal(() => instance.SupplyApprovers(F.S2, Resolved(ApproverSourceKind.ApplicationOwner, F.B), instance.Version, T0)).ShouldBe(ApprovalRefusal.StageNotAwaitingApprovers);

        instance.SupplyApprovers(F.S1, Resolved(ApproverSourceKind.NamedUsers, F.A), instance.Version, T0);
        instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, null, instance.Version), T0.AddHours(1));
        instance.View(F.S2).State.ShouldBe(StageState.AwaitingApprovers);
        instance.View(F.S2).DeadlineUtc.ShouldBe(T0.AddHours(5));
        instance.SupplyApprovers(F.S2, Resolved(ApproverSourceKind.ApplicationOwner, F.B), instance.Version, T0.AddHours(1));
        instance.Decide(new ApprovalDecision(F.Id(2), F.S2, F.B, null, true, null, instance.Version), T0.AddHours(2));
        instance.State.ShouldBe(ApprovalState.Approved);
    }

    [Fact]
    public void ApproversAreSuppliedOnce()
    {
        var instance = Bound(Level(F.S1, 1, ApproverSource.Workgroup("grp_a")));
        instance.SupplyApprovers(F.S1, Resolved(ApproverSourceKind.Workgroup, F.A), instance.Version, T0);
        var version = instance.Version;
        F.Refusal(() => instance.SupplyApprovers(F.S1, Resolved(ApproverSourceKind.Workgroup, F.B), version, T0)).ShouldBe(ApprovalRefusal.StageNotAwaitingApprovers);
        instance.Version.ShouldBe(version);
        instance.View(F.S1).Candidates.ShouldBe([F.A]);
    }

    [Fact]
    public void AResolutionThatFoundNobodyHoldsTheLevelAndNeverApproves()
    {
        var instance = Bound(Level(F.S1, 1, ApproverSource.Workgroup("grp_a")));
        instance.SupplyApprovers(F.S1, Failed(ApproverSourceKind.Workgroup, ResolutionOutcome.NobodyAvailable), instance.Version, T0);
        instance.View(F.S1).State.ShouldBe(StageState.Held);
        instance.State.ShouldBe(ApprovalState.Held);
        instance.View(F.S1).Candidates.ShouldBeEmpty();
        instance.ProvisioningAuthorized.ShouldBeFalse();

        // Someone with the escalate permission (slice 3) can still give it people.
        instance.Escalate(F.S1, [F.C], F.E, "r", instance.Version, T0.AddHours(1));
        instance.View(F.S1).State.ShouldBe(StageState.Active);
    }

    [Theory]
    [InlineData(ApproverSourceKind.Manager)]
    [InlineData(ApproverSourceKind.ManagerOfManager)]
    [InlineData(ApproverSourceKind.EntitlementOwner)]
    [InlineData(ApproverSourceKind.Matrix)]
    public void ASourceWithNoDataHoldsAndCanBeResolvedNoOtherWay(ApproverSourceKind kind)
    {
        var instance = Bound(Level(F.S1, 1, ApproverSource.Unbuilt(kind)));
        F.Refusal(() => instance.SupplyApprovers(F.S1, Resolved(kind, F.A), instance.Version, T0)).ShouldBe(ApprovalRefusal.InvalidResolution);
        F.Refusal(() => instance.SupplyApprovers(F.S1, Failed(kind, ResolutionOutcome.NobodyAvailable), instance.Version, T0)).ShouldBe(ApprovalRefusal.InvalidResolution);
        instance.SupplyApprovers(F.S1, Failed(kind, ResolutionOutcome.SourceUnavailable, ResolutionStepResult.Unavailable), instance.Version, T0);
        instance.View(F.S1).State.ShouldBe(StageState.Held);
    }

    [Fact]
    public void ResolutionsThatDontFitTheLevelAreRefusedAndChangeNothing()
    {
        var instance = Bound(Level(F.S1, 1, ApproverSource.ApplicationRole(App, Role)));
        var version = instance.Version;
        ApproverResolution[] bad =
        [
            Resolved(ApproverSourceKind.ApplicationOwner, F.A), // the first step must be the level's own source
            Resolved(ApproverSourceKind.ApplicationRole, F.Requester), // excluded people are never supplied
            Resolved(ApproverSourceKind.ApplicationRole, F.Beneficiary),
            Resolved(ApproverSourceKind.ApplicationRole, F.PolicyMaker),
            Resolved(ApproverSourceKind.ApplicationRole), // resolved with nobody
            Resolved(ApproverSourceKind.ApplicationRole, F.A, F.A),
            Resolved(ApproverSourceKind.ApplicationRole, default(PersonId)),
            Resolved(ApproverSourceKind.ApplicationRole, F.A) with { Steps = [] },
            Resolved(ApproverSourceKind.ApplicationRole, F.A) with { Steps = [.. Enumerable.Repeat(new ResolutionStep(ApproverSourceKind.ApplicationRole, ResolutionStepResult.Found, null, null, null, 1, 0), ApproverResolution.MaxSteps + 1)] },
            Resolved(ApproverSourceKind.ApplicationRole, F.A) with { Steps = [null!] },
            Resolved(ApproverSourceKind.ApplicationRole, F.A) with { Outcome = (ResolutionOutcome)99 },
            Failed(ApproverSourceKind.ApplicationRole, ResolutionOutcome.NobodyAvailable) with { People = [F.A] },
            Failed(ApproverSourceKind.ApplicationRole, ResolutionOutcome.SourceUnavailable),
            new(ResolutionOutcome.Resolved, [F.A], [new ResolutionStep(ApproverSourceKind.ApplicationRole, ResolutionStepResult.Found, null, null, null, 1, 2)]),
            new(ResolutionOutcome.Resolved, [F.A], [new ResolutionStep(ApproverSourceKind.ApplicationRole, ResolutionStepResult.Found, null, 0, null, 1, 0)]),
            new(ResolutionOutcome.Resolved, [F.A], [new ResolutionStep(ApproverSourceKind.ApplicationRole, ResolutionStepResult.Found, null, null, "ABC", 1, 0)]),
            new(ResolutionOutcome.Resolved, [F.A], [new ResolutionStep(ApproverSourceKind.ApplicationRole, ResolutionStepResult.Found, "a b", null, null, 1, 0)]),
            new(ResolutionOutcome.Resolved, [F.A], [new ResolutionStep(ApproverSourceKind.ApplicationRole, (ResolutionStepResult)99, null, null, null, 1, 0)]),
        ];
        foreach (var resolution in bad)
        {
            F.Refusal(() => instance.SupplyApprovers(F.S1, resolution, version, T0)).ShouldBe(ApprovalRefusal.InvalidResolution);
        }

        F.Refusal(() => instance.SupplyApprovers(F.Id(999), Resolved(ApproverSourceKind.ApplicationRole, F.A), version, T0)).ShouldBe(ApprovalRefusal.UnknownStage);
        F.Refusal(() => instance.SupplyApprovers(F.S1, Resolved(ApproverSourceKind.ApplicationRole, F.A), version - 1, T0)).ShouldBe(ApprovalRefusal.StaleVersion);
        instance.Version.ShouldBe(version);
        instance.View(F.S1).State.ShouldBe(StageState.AwaitingApprovers);

        // A required role that was empty is a role's outcome only.
        instance.SupplyApprovers(F.S1, Failed(ApproverSourceKind.ApplicationRole, ResolutionOutcome.RequiredRoleEmpty), version, T0);
        instance.View(F.S1).State.ShouldBe(StageState.Held);
        var other = Bound(Level(F.S1, 1, ApproverSource.ApplicationOwner(App)));
        F.Refusal(() => other.SupplyApprovers(F.S1, Failed(ApproverSourceKind.ApplicationOwner, ResolutionOutcome.RequiredRoleEmpty), other.Version, T0)).ShouldBe(ApprovalRefusal.InvalidResolution);
    }

    [Fact]
    public void ACallersResolutionCannotChangeTheLevelAfterItIsSupplied()
    {
        var instance = Bound(Level(F.S1, 1, ApproverSource.Workgroup("grp_a"), QuorumMode.All));
        var people = new[] { F.A, F.B };
        var steps = new[] { new ResolutionStep(ApproverSourceKind.Workgroup, ResolutionStepResult.Found, "grp_a", 1, null, 2, 0) };
        instance.SupplyApprovers(F.S1, new ApproverResolution(ResolutionOutcome.Resolved, people, steps), instance.Version, T0);
        people[1] = F.C;
        steps[0] = steps[0] with { Found = 9 };
        instance.View(F.S1).Candidates.ShouldBe([F.A, F.B]);
        instance.View(F.S1).Resolution!.People.ShouldBe([F.A, F.B]);
        instance.View(F.S1).Resolution!.Steps[0].Found.ShouldBe(2);
    }

    [Fact]
    public void TooLateIsTooLateAndTheTimeoutApplies()
    {
        var escalating = Bound(Level(F.S1, 1, ApproverSource.Workgroup("grp_a")));
        F.Refusal(() => escalating.SupplyApprovers(F.S1, Resolved(ApproverSourceKind.Workgroup, F.A), escalating.Version, T0.AddHours(4))).ShouldBe(ApprovalRefusal.StagePastDeadline);
        escalating.ApplyTimeouts(T0.AddHours(4), escalating.Version);
        escalating.View(F.S1).State.ShouldBe(StageState.Held);
        escalating.State.ShouldBe(ApprovalState.Held);

        var rejecting = Bound(Level(F.S1, 1, ApproverSource.Workgroup("grp_a"), onTimeout: TimeoutAction.Reject));
        rejecting.ApplyTimeouts(T0.AddHours(4), rejecting.Version);
        rejecting.State.ShouldBe(ApprovalState.Rejected);

        var cancelling = Bound(Level(F.S1, 1, ApproverSource.Workgroup("grp_a"), onTimeout: TimeoutAction.Cancel));
        cancelling.ApplyTimeouts(T0.AddHours(4), cancelling.Version);
        cancelling.State.ShouldBe(ApprovalState.Cancelled);
        cancelling.View(F.S1).State.ShouldBe(StageState.Cancelled);
    }

    [Fact]
    public void CancellingOrWithdrawingClosesAWaitingLevel()
    {
        var instance = Bound(Level(F.S1, 1, ApproverSource.Workgroup("grp_a")));
        instance.Withdraw(F.Requester, null, instance.Version, T0);
        instance.View(F.S1).State.ShouldBe(StageState.Cancelled);
        F.Refusal(() => instance.SupplyApprovers(F.S1, Resolved(ApproverSourceKind.Workgroup, F.A), instance.Version, T0)).ShouldBe(ApprovalRefusal.InstanceClosed);
    }

    [Fact]
    public void AMinimumNumberIsCheckedAgainstWhoWasFound()
    {
        // The route can't know how many a workgroup has, so it allows up to the limit; too few found holds the level.
        Should.NotThrow(() => ApprovalRoute.Create([Level(F.S1, 1, ApproverSource.Workgroup("grp_a"), QuorumMode.MinimumN, ApprovalLimits.MaxCandidatesPerStage)]));
        F.Refusal(() => ApprovalRoute.Create([Level(F.S1, 1, ApproverSource.Workgroup("grp_a"), QuorumMode.MinimumN, ApprovalLimits.MaxCandidatesPerStage + 1)])).ShouldBe(ApprovalRefusal.InvalidQuorum);
        F.Refusal(() => ApprovalRoute.Create([Level(F.S1, 1, ApproverSource.Named(F.A), QuorumMode.MinimumN, 2)])).ShouldBe(ApprovalRefusal.InvalidQuorum);

        var instance = Bound(Level(F.S1, 1, ApproverSource.Workgroup("grp_a"), QuorumMode.MinimumN, 3));
        instance.SupplyApprovers(F.S1, Resolved(ApproverSourceKind.Workgroup, F.A, F.B), instance.Version, T0);
        instance.View(F.S1).State.ShouldBe(StageState.Held);
    }

    [Fact]
    public void TheSameApproverIsCarriedWhenTheNextLevelGetsItsPeople()
    {
        var instance = Bound(Level(F.S1, 1, ApproverSource.Named(F.A)), Level(F.S2, 2, ApproverSource.Workgroup("grp_a")));
        instance.SupplyApprovers(F.S1, Resolved(ApproverSourceKind.NamedUsers, F.A), instance.Version, T0);
        instance.Decide(new ApprovalDecision(F.Id(1), F.S1, F.A, null, true, null, instance.Version), T0);
        instance.View(F.S2).Approvers.ShouldBeEmpty();
        instance.SupplyApprovers(F.S2, Resolved(ApproverSourceKind.Workgroup, F.A, F.B), instance.Version, T0);
        instance.Decisions[^1].Outcome.ShouldBe(RecordedOutcome.ApprovedAutomatically);
        instance.State.ShouldBe(ApprovalState.Approved);
    }

    public static TheoryData<ApproverSource> BadSources() =>
    [
        new ApproverSource(ApproverSourceKind.NamedUsers, []),
        new ApproverSource(ApproverSourceKind.NamedUsers, [F.A], WorkgroupKey: "grp_a"),
        new ApproverSource(ApproverSourceKind.Workgroup, []),
        new ApproverSource(ApproverSourceKind.Workgroup, [], WorkgroupKey: "Grp_A"),
        new ApproverSource(ApproverSourceKind.Workgroup, [], WorkgroupKey: "a"),
        new ApproverSource(ApproverSourceKind.Workgroup, [], WorkgroupKey: new string('a', 65)),
        new ApproverSource(ApproverSourceKind.Workgroup, [F.A], WorkgroupKey: "grp_a"),
        new ApproverSource(ApproverSourceKind.ApplicationOwner, []),
        new ApproverSource(ApproverSourceKind.ApplicationOwner, [], ApplicationId: Guid.Empty),
        new ApproverSource(ApproverSourceKind.ApplicationOwner, [], ApplicationId: App, RoleTypeId: Role),
        new ApproverSource(ApproverSourceKind.ApplicationRole, [], ApplicationId: App),
        new ApproverSource(ApproverSourceKind.ApplicationRole, [], ApplicationId: App, RoleTypeId: Guid.Empty),
        new ApproverSource(ApproverSourceKind.Manager, [], ApplicationId: App),
        new ApproverSource(ApproverSourceKind.Matrix, [F.A]),
        new ApproverSource(ApproverSourceKind.FallbackWorkgroup, [], WorkgroupKey: "iam_managers"),
        new ApproverSource((ApproverSourceKind)99, []),
    ];

    [Theory]
    [MemberData(nameof(BadSources))]
    public void ASourceMustHaveExactlyTheFieldsItsKindNeeds(ApproverSource source)
    {
        F.Refusal(() => ApprovalRoute.Create([Level(F.S1, 1, source)])).ShouldBe(ApprovalRefusal.InvalidCandidates);
    }

    [Fact]
    public void LoggingASourceOrResolutionNamesNobody()
    {
        ApproverSource.Named(F.A).ToString().ShouldNotContain("person");
        Resolved(ApproverSourceKind.NamedUsers, F.A).ToString().ShouldNotContain("person");
        F.A.ToString().ShouldBe(nameof(PersonId));
    }
}

/// <summary>The resolution chain (DL-068d, DL-069c, DL-069d), against in-memory directories.</summary>
public sealed class ApproverResolverTests
{
    private static readonly Guid App = F.Id(700);
    private static readonly Guid Role = F.Id(701);
    private static readonly string Hash = new('c', 64);
    private static readonly HashSet<PersonId> Excluded = [F.Requester, F.Beneficiary, F.PolicyMaker];

    private readonly FakeApplications _applications = new();
    private readonly FakeWorkgroups _workgroups = new();

    private ApproverResolver Resolver(string? fallback = null) => new(_applications, _workgroups, new ApproverResolutionSettings(fallback));

    private Task<ApproverResolution> Resolve(ApproverSource source) => Resolver().ResolveAsync(source, Excluded, TestContext.Current.CancellationToken);

    private void Application(string owner, params ApplicationRoleHolding[] roles) =>
        _applications.Items[App] = new ApplicationApprovers(App, 3, Hash, owner, roles);

    private void Workgroup(string key, params PersonId[] members) =>
        _workgroups.Items[key] = new WorkgroupMembership(key, 2, new string('d', 64), [.. members.Select(m => m.Value)]);

    private static ApplicationRoleHolding Holding(ApplicationRoleRequirement requirement, ApplicationRoleHolderKind? kind = null, string? holder = null) =>
        new(Role, requirement, kind, holder);

    private static ResolutionStepResult[] Results(ApproverResolution resolution) => [.. resolution.Steps.Select(s => s.Result)];

    private static ApproverSourceKind[] Sources(ApproverResolution resolution) => [.. resolution.Steps.Select(s => s.Source)];

    [Fact]
    public async Task NamedUsersLessTheExcludedOnes()
    {
        var resolution = await Resolve(ApproverSource.Named(F.A, F.Requester));
        resolution.Outcome.ShouldBe(ResolutionOutcome.Resolved);
        resolution.People.ShouldBe([F.A]);
        resolution.Steps.Single().ShouldBe(new ResolutionStep(ApproverSourceKind.NamedUsers, ResolutionStepResult.Found, null, null, null, 2, 1));
    }

    [Fact]
    public async Task AWorkgroupsActiveMembersWithItsVersionAsEvidence()
    {
        Workgroup("grp_a", F.A, F.B, F.Beneficiary);
        var resolution = await Resolve(ApproverSource.Workgroup("grp_a"));
        resolution.People.ShouldBe([F.A, F.B]);
        resolution.Steps.Single().ShouldBe(new ResolutionStep(ApproverSourceKind.Workgroup, ResolutionStepResult.Found, "grp_a", 2, new string('d', 64), 3, 1));
    }

    [Fact]
    public async Task TheApplicationOwnerFromTheActiveSettings()
    {
        Application(F.C.Value);
        var resolution = await Resolve(ApproverSource.ApplicationOwner(App));
        resolution.People.ShouldBe([F.C]);
        resolution.Steps.Single().ShouldBe(new ResolutionStep(ApproverSourceKind.ApplicationOwner, ResolutionStepResult.Found, App.ToString("D"), 3, Hash, 1, 0));
    }

    [Fact]
    public async Task ARoleHeldByAPerson()
    {
        Application(F.C.Value, Holding(ApplicationRoleRequirement.Optional, ApplicationRoleHolderKind.Person, F.D.Value));
        var resolution = await Resolve(ApproverSource.ApplicationRole(App, Role));
        resolution.People.ShouldBe([F.D]);
        resolution.Steps.Single().Reference.ShouldBe($"{App:D}:{Role:D}");
    }

    [Fact]
    public async Task ARoleHeldByAWorkgroupRecordsBothVersions()
    {
        Application(F.C.Value, Holding(ApplicationRoleRequirement.Required, ApplicationRoleHolderKind.Group, "grp_it"));
        Workgroup("grp_it", F.A, F.B);
        var resolution = await Resolve(ApproverSource.ApplicationRole(App, Role));
        resolution.People.ShouldBe([F.A, F.B]);
        Sources(resolution).ShouldBe([ApproverSourceKind.ApplicationRole, ApproverSourceKind.Workgroup]);
        resolution.Steps[0].Version.ShouldBe(3);
        resolution.Steps[1].Version.ShouldBe(2);
    }

    [Fact]
    public async Task AnEmptyOptionalRoleGoesToTheOwner()
    {
        Application(F.C.Value, Holding(ApplicationRoleRequirement.Optional));
        var resolution = await Resolve(ApproverSource.ApplicationRole(App, Role));
        resolution.People.ShouldBe([F.C]);
        Sources(resolution).ShouldBe([ApproverSourceKind.ApplicationRole, ApproverSourceKind.ApplicationOwner]);
        Results(resolution).ShouldBe([ResolutionStepResult.Empty, ResolutionStepResult.Found]);
    }

    [Fact]
    public async Task AnEmptyRequiredRoleHoldsAndIsFilledByNobodyElse()
    {
        Application(F.C.Value, Holding(ApplicationRoleRequirement.Required));
        Workgroup("iam_managers", F.E);
        var resolution = await Resolve(ApproverSource.ApplicationRole(App, Role));
        resolution.Outcome.ShouldBe(ResolutionOutcome.RequiredRoleEmpty);
        resolution.People.ShouldBeEmpty();
        Results(resolution).ShouldBe([ResolutionStepResult.Empty]);
    }

    [Fact]
    public async Task ARoleWhoseHoldersAreAllExcludedGoesToTheOwnerThenTheFallback()
    {
        Application(F.Beneficiary.Value, Holding(ApplicationRoleRequirement.Required, ApplicationRoleHolderKind.Person, F.Requester.Value));
        Workgroup("iam_managers", F.E, F.PolicyMaker);
        var resolution = await Resolve(ApproverSource.ApplicationRole(App, Role));
        resolution.Outcome.ShouldBe(ResolutionOutcome.Resolved);
        resolution.People.ShouldBe([F.E]);
        Sources(resolution).ShouldBe([ApproverSourceKind.ApplicationRole, ApproverSourceKind.ApplicationOwner, ApproverSourceKind.FallbackWorkgroup]);
        Results(resolution).ShouldBe([ResolutionStepResult.AllExcluded, ResolutionStepResult.AllExcluded, ResolutionStepResult.Found]);
    }

    [Fact]
    public async Task AnEmptyFallbackHoldsTheLevel()
    {
        Application(F.Beneficiary.Value);
        Workgroup("iam_managers");
        var resolution = await Resolve(ApproverSource.ApplicationOwner(App));
        resolution.Outcome.ShouldBe(ResolutionOutcome.NobodyAvailable);
        resolution.People.ShouldBeEmpty();
        Results(resolution).ShouldBe([ResolutionStepResult.AllExcluded, ResolutionStepResult.Empty]);
    }

    [Fact]
    public async Task MissingDataIsAStepNeverAnError()
    {
        // No application, no workgroup, and no fallback with active members: every step is recorded, and the level holds.
        (await Resolve(ApproverSource.ApplicationOwner(App))).Steps.Select(s => (s.Source, s.Result)).ShouldBe(
            [(ApproverSourceKind.ApplicationOwner, ResolutionStepResult.NotFound), (ApproverSourceKind.FallbackWorkgroup, ResolutionStepResult.NotFound)]);
        (await Resolve(ApproverSource.ApplicationRole(App, Role))).Outcome.ShouldBe(ResolutionOutcome.NobodyAvailable);
        (await Resolve(ApproverSource.Workgroup("grp_none"))).Outcome.ShouldBe(ResolutionOutcome.NobodyAvailable);

        // A role the application doesn't have goes to the owner, as an empty optional role does.
        Application(F.C.Value);
        var missingRole = await Resolve(ApproverSource.ApplicationRole(App, Role));
        missingRole.People.ShouldBe([F.C]);
        Results(missingRole).ShouldBe([ResolutionStepResult.NotFound, ResolutionStepResult.Found]);
    }

    [Fact]
    public async Task AnEmptyWorkgroupOrAllExcludedNamedUsersGoToTheFallback()
    {
        Workgroup("grp_a");
        Workgroup("iam_managers", F.E);
        (await Resolve(ApproverSource.Workgroup("grp_a"))).People.ShouldBe([F.E]);
        (await Resolve(ApproverSource.Named(F.Requester))).People.ShouldBe([F.E]);
    }

    [Fact]
    public async Task TheFallbackIsNotAskedTwice()
    {
        Workgroup("iam_managers", F.Requester);
        var resolution = await Resolve(ApproverSource.Workgroup("iam_managers"));
        resolution.Outcome.ShouldBe(ResolutionOutcome.NobodyAvailable);
        resolution.Steps.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(ApproverSourceKind.Manager)]
    [InlineData(ApproverSourceKind.ManagerOfManager)]
    [InlineData(ApproverSourceKind.EntitlementOwner)]
    [InlineData(ApproverSourceKind.Matrix)]
    public async Task ASourceWithNoDataIsUnavailableWithNoFallback(ApproverSourceKind kind)
    {
        Workgroup("iam_managers", F.E);
        var resolution = await Resolve(ApproverSource.Unbuilt(kind));
        resolution.Outcome.ShouldBe(ResolutionOutcome.SourceUnavailable);
        resolution.People.ShouldBeEmpty();
        Results(resolution).ShouldBe([ResolutionStepResult.Unavailable]);
    }

    [Fact]
    public async Task AWorkgroupWithADamagedMemberGivesNobodyFromIt()
    {
        _workgroups.Items["grp_a"] = new WorkgroupMembership("grp_a", 1, new string('d', 64), [F.A.Value, " bad"]);
        var resolution = await Resolve(ApproverSource.Workgroup("grp_a"));
        resolution.Outcome.ShouldBe(ResolutionOutcome.NobodyAvailable);
        resolution.Steps[0].Result.ShouldBe(ResolutionStepResult.NotFound);
    }

    [Fact]
    public async Task TheFallbackWorkgroupIsConfigured()
    {
        Workgroup("iam_managers", F.E);
        Workgroup("sec_ops", F.D);
        var resolution = await Resolver("sec_ops").ResolveAsync(ApproverSource.Named(F.Requester), Excluded, TestContext.Current.CancellationToken);
        resolution.People.ShouldBe([F.D]);
        resolution.Steps[^1].Reference.ShouldBe("sec_ops");
        Should.Throw<ArgumentException>(() => new ApproverResolutionSettings("Not A Key"));
        new ApproverResolutionSettings().FallbackWorkgroup.ShouldBe("iam_managers");
    }

    [Fact]
    public async Task EveryResolutionIsOneTheInstanceAccepts()
    {
        // Whatever the data, the resolver's answer must pass the instance's own checks.
        Application(F.Beneficiary.Value, Holding(ApplicationRoleRequirement.Optional, ApplicationRoleHolderKind.Group, "grp_it"));
        Workgroup("grp_it", F.Requester);
        Workgroup("iam_managers", F.E);
        ApproverSource[] sources =
        [
            ApproverSource.Named(F.A), ApproverSource.Named(F.Requester), ApproverSource.Workgroup("grp_it"), ApproverSource.Workgroup("grp_none"),
            ApproverSource.ApplicationOwner(App), ApproverSource.ApplicationRole(App, Role), ApproverSource.ApplicationRole(App, F.Id(9)),
            ApproverSource.Unbuilt(ApproverSourceKind.Matrix),
        ];
        foreach (var source in sources)
        {
            var instance = ApprovalInstance.Bind(F.Binding, ApprovalRoute.Create([new StageDefinition(F.S1, 1, QuorumMode.AnyOne, null, false, false, true, TimeSpan.FromHours(1), TimeoutAction.Escalate, source)]), F.T0);
            var resolution = await Resolve(source);
            Should.NotThrow(() => instance.SupplyApprovers(F.S1, resolution, instance.Version, F.T0));
        }
    }

    private sealed class FakeApplications : IApplicationApproverDirectory
    {
        public Dictionary<Guid, ApplicationApprovers> Items { get; } = [];

        public Task<ApplicationApprovers?> FindActiveAsync(Guid applicationId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.GetValueOrDefault(applicationId));
    }

    private sealed class FakeWorkgroups : IWorkgroupDirectory
    {
        public Dictionary<string, WorkgroupMembership> Items { get; } = [];

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Items.ContainsKey(key));

        public Task<WorkgroupMembership?> FindActiveAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Items.GetValueOrDefault(key));
    }
}
