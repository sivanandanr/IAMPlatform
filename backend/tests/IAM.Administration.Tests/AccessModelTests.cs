using IAM.Modules.Administration.Domain.Access;
using Shouldly;

namespace IAM.Administration.Tests;

/// <summary>Synthetic people. Nothing here is a real person or identity provider id.</summary>
internal static class P
{
    public const string Admin = "sub-admin";
    public const string Manager = "sub-manager";
    public const string Second = "sub-second-manager";
    public const string Alice = "sub-alice";
    public const string Bob = "sub-bob";
    public static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private static int _id = 1000;

    public static Guid NewId() => new($"00000000-0000-0000-0000-{Interlocked.Increment(ref _id):D12}");

    public static AccessModel Started() => AccessModel.Defaults().Bootstrap(Admin, Manager);

    /// <summary>A model with a second IAM Manager, so the first can be the subject of changes.</summary>
    public static AccessModel WithSecondManager()
    {
        var model = Started();
        var change = ChangeRequest.Propose(NewId(), new AssignRole(Second, AccessModel.IamManager), Admin, "Cover", model, T0);
        return change.Approve(Manager, null, model, T0);
    }

    public static AccessRefusal? Refusal(Action action) => Should.Throw<AccessRefusedException>(action).Refusal;

    public static AccessModel ProposeAndApprove(AccessModel model, AccessChange change, string maker = Admin, string checker = Manager) =>
        ChangeRequest.Propose(NewId(), change, maker, "Needed", model, T0).Approve(checker, null, model, T0);
}

public sealed class AccessModelTests
{
    [Fact]
    public void DefaultsHaveTheAgreedRolesAndPairsAndNobodyAssigned()
    {
        var model = AccessModel.Defaults();
        model.Roles.Select(r => r.Code).ShouldBe([AccessModel.Auditor, AccessModel.BreakGlassCustodian, AccessModel.IamAdministrator, AccessModel.IamManager, AccessModel.PolicyAuthor, AccessModel.PolicyChecker]);
        model.Assignments.ShouldBeEmpty();
        model.IncompatiblePairs.ShouldBe([
            IncompatiblePair.Of(AccessModel.Auditor, AccessModel.IamAdministrator),
            IncompatiblePair.Of(AccessModel.IamAdministrator, AccessModel.IamManager),
            IncompatiblePair.Of(AccessModel.PolicyAuthor, AccessModel.PolicyChecker),
        ]);
        model.Roles.Single(r => r.Code == AccessModel.BreakGlassCustodian).Assignable.ShouldBeFalse();
    }

    [Fact]
    public void AnAdministratorProposesAndAManagerApprovesButNotTheOtherWayRound()
    {
        var model = P.Started();
        model.Can(P.Admin, ActionCatalog.ChangesPropose).ShouldBeTrue();
        model.Can(P.Admin, ActionCatalog.ChangesApprove).ShouldBeFalse();
        model.Can(P.Manager, ActionCatalog.ChangesApprove).ShouldBeTrue();
        model.Can(P.Manager, ActionCatalog.ChangesPropose).ShouldBeFalse();
        model.Can(P.Manager, ActionCatalog.ApprovalsEscalate).ShouldBeTrue();
    }

    [Fact]
    public void EveryoneSignedInCanReadThemselvesAndNothingElse()
    {
        var model = P.Started();
        model.ActionsOf("sub-stranger").ShouldBe(ActionCatalog.EveryoneActions, ignoreOrder: true);
        model.Can("sub-stranger", ActionCatalog.RolesRead).ShouldBeFalse();
    }

    [Theory]
    [InlineData("administration.everything")]
    [InlineData("")]
    [InlineData("ADMINISTRATION.CHANGES.APPROVE")]
    public void AnActionNotInTheCatalogIsNeverAllowed(string action) =>
        P.Started().Can(P.Manager, action).ShouldBeFalse();

    [Fact]
    public void BootstrapNeedsTwoDifferentPeopleAndWorksOnlyOnce()
    {
        P.Refusal(() => AccessModel.Defaults().Bootstrap(P.Admin, P.Admin)).ShouldBe(AccessRefusal.SameBootstrapPerson);
        P.Refusal(() => AccessModel.Defaults().Bootstrap(" ", P.Manager)).ShouldBe(AccessRefusal.InvalidSubject);
        P.Refusal(() => P.Started().Bootstrap(P.Alice, P.Bob)).ShouldBe(AccessRefusal.AlreadyBootstrapped);
    }

    [Fact]
    public void ARoleCanOnlyGiveCatalogActionsAndNeverAnEveryoneAction()
    {
        var model = P.Started();
        P.Refusal(() => model.Check(new DefineRole("helpdesk", "Helpdesk", ["administration.everything"]))).ShouldBe(AccessRefusal.UnknownAction);
        P.Refusal(() => model.Check(new DefineRole("helpdesk", "Helpdesk", [ActionCatalog.SelfRead]))).ShouldBe(AccessRefusal.ActionNotGrantable);
        P.Refusal(() => model.Check(new DefineRole("helpdesk", "Helpdesk", [ActionCatalog.RolesRead, ActionCatalog.RolesRead]))).ShouldBe(AccessRefusal.DuplicateAction);
        P.Refusal(() => model.Check(new DefineRole("helpdesk", "Helpdesk", ["ADMINISTRATION.ROLES.READ"]))).ShouldBe(AccessRefusal.UnknownAction);
        Should.NotThrow(() => model.Check(new DefineRole("helpdesk", "Helpdesk", [ActionCatalog.RolesRead])));
    }

    [Theory]
    [InlineData("Helpdesk")]
    [InlineData("help desk")]
    [InlineData("-helpdesk")]
    [InlineData("helpdesk-")]
    [InlineData("help--desk")]
    [InlineData("1helpdesk")]
    [InlineData("helpDesk")]
    [InlineData("")]
    public void RoleCodesAreCodesNotText(string code) =>
        P.Refusal(() => P.Started().Check(new DefineRole(code, "Helpdesk", []))).ShouldBe(AccessRefusal.InvalidRoleCode);

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("a{0}b", 0x202E)]
    [InlineData("a{0}b", 0x000A)]
    [InlineData("a{0}b", 0x2028)]
    public void RoleNamesAreSafeText(string template, int character)
    {
        var name = template.Replace("{0}", ((char)character).ToString(), StringComparison.Ordinal);
        P.Refusal(() => P.Started().Check(new DefineRole("helpdesk", name, []))).ShouldBe(AccessRefusal.InvalidRoleName);
    }

    [Fact]
    public void TheBreakGlassCustodianCanBeNeitherAssignedNorRedefined()
    {
        var model = P.Started();
        P.Refusal(() => model.Check(new AssignRole(P.Alice, AccessModel.BreakGlassCustodian))).ShouldBe(AccessRefusal.RoleNotAssignable);
        P.Refusal(() => model.Check(new DefineRole(AccessModel.BreakGlassCustodian, "Custodian", [ActionCatalog.RolesRead]))).ShouldBe(AccessRefusal.ReservedRole);
    }

    [Fact]
    public void IncompatibleRolesCanNotBeHeldTogetherEvenThroughSeparateChanges()
    {
        var model = P.ProposeAndApprove(P.Started(), new AssignRole(P.Alice, AccessModel.PolicyAuthor));
        P.Refusal(() => model.Check(new AssignRole(P.Alice, AccessModel.PolicyChecker))).ShouldBe(AccessRefusal.IncompatibleRoles);
        P.Refusal(() => model.Check(new AssignRole(P.Admin, AccessModel.IamManager))).ShouldBe(AccessRefusal.IncompatibleRoles);
        P.Refusal(() => model.Check(new AssignRole(P.Admin, AccessModel.Auditor))).ShouldBe(AccessRefusal.IncompatibleRoles);
    }

    [Fact]
    public void ANewPairIsRefusedWhileSomeoneHoldsBoth()
    {
        var model = P.ProposeAndApprove(P.Started(), new AssignRole(P.Alice, AccessModel.Auditor));
        model = P.ProposeAndApprove(model, new AssignRole(P.Alice, AccessModel.PolicyAuthor));
        P.Refusal(() => model.Check(new AddIncompatiblePair(AccessModel.Auditor, AccessModel.PolicyAuthor))).ShouldBe(AccessRefusal.PairConflictsWithAssignments);
        P.Refusal(() => model.Check(new AddIncompatiblePair(AccessModel.Auditor, AccessModel.Auditor))).ShouldBe(AccessRefusal.SameRoleTwice);
        P.Refusal(() => model.Check(new AddIncompatiblePair(AccessModel.IamManager, AccessModel.IamAdministrator))).ShouldBe(AccessRefusal.PairAlreadyExists);
        P.Refusal(() => model.Check(new RemoveIncompatiblePair(AccessModel.Auditor, AccessModel.PolicyAuthor))).ShouldBe(AccessRefusal.UnknownPair);
    }

    [Fact]
    public void AChangeThatWouldLeaveNobodyToProposeOrADifferentPersonToApproveIsRefused()
    {
        var model = P.Started();
        P.Refusal(() => model.Check(new RemoveAssignment(P.Manager, AccessModel.IamManager))).ShouldBe(AccessRefusal.WouldLockOut);
        P.Refusal(() => model.Check(new RemoveAssignment(P.Admin, AccessModel.IamAdministrator))).ShouldBe(AccessRefusal.WouldLockOut);
        P.Refusal(() => model.Check(new DefineRole(AccessModel.IamManager, "IAM Manager", [ActionCatalog.RolesRead]))).ShouldBe(AccessRefusal.WouldLockOut);

        // Nobody can hold both duties, so one person can never be both the proposer and the approver that are left.
        var both = P.ProposeAndApprove(P.Started(), new DefineRole("approver", "Approver", [ActionCatalog.ChangesApprove]));
        P.Refusal(() => both.Check(new AssignRole(P.Admin, "approver"))).ShouldBe(AccessRefusal.SeparatedDuties);

        // With a second manager, either one can go.
        var covered = P.WithSecondManager();
        Should.NotThrow(() => covered.Check(new RemoveAssignment(P.Manager, AccessModel.IamManager)));
    }

    [Fact]
    public void AssignmentsAndRemovalsMustMakeSense()
    {
        var model = P.Started();
        P.Refusal(() => model.Check(new AssignRole(P.Admin, AccessModel.IamAdministrator))).ShouldBe(AccessRefusal.AlreadyAssigned);
        P.Refusal(() => model.Check(new AssignRole(P.Alice, "no-such-role"))).ShouldBe(AccessRefusal.UnknownRole);
        P.Refusal(() => model.Check(new RemoveAssignment(P.Alice, AccessModel.Auditor))).ShouldBe(AccessRefusal.NotAssigned);
        P.Refusal(() => model.Check(new AssignRole("", AccessModel.Auditor))).ShouldBe(AccessRefusal.InvalidSubject);
        P.Refusal(() => model.Check(new AssignRole(" sub", AccessModel.Auditor))).ShouldBe(AccessRefusal.InvalidSubject);
        P.Refusal(() => model.Check(new AssignRole(new string('x', AccessLimits.MaxSubjectLength + 1), AccessModel.Auditor))).ShouldBe(AccessRefusal.InvalidSubject);
    }

    [Fact]
    public void ARolesActionsTakeEffectForEveryoneHoldingItAndRemovalIsImmediate()
    {
        var model = P.ProposeAndApprove(P.Started(), new AssignRole(P.Alice, AccessModel.Auditor));
        model.Can(P.Alice, ActionCatalog.ChangesRead).ShouldBeTrue();
        model = P.ProposeAndApprove(model, new DefineRole(AccessModel.Auditor, "Auditor", [ActionCatalog.RolesRead]));
        model.Can(P.Alice, ActionCatalog.ChangesRead).ShouldBeFalse();
        model = P.ProposeAndApprove(model, new RemoveAssignment(P.Alice, AccessModel.Auditor));
        model.Can(P.Alice, ActionCatalog.RolesRead).ShouldBeFalse();
    }

    [Fact]
    public void EveryAppliedChangeMakesANewVersionAndLeavesTheOldModelAlone()
    {
        var before = P.Started();
        var after = P.ProposeAndApprove(before, new AssignRole(P.Alice, AccessModel.Auditor));
        after.Version.ShouldBe(before.Version + 1);
        before.RolesOf(P.Alice).ShouldBeEmpty();
        after.RolesOf(P.Alice).ShouldBe([AccessModel.Auditor]);
    }

    [Fact]
    public void ALoadedModelIsCheckedAgain()
    {
        var model = P.Started();
        P.Refusal(() => AccessModel.Load(model.Roles, [.. model.Assignments, new Assignment(P.Admin, AccessModel.IamManager)], model.IncompatiblePairs, 5)).ShouldBe(AccessRefusal.IncompatibleRoles);
        P.Refusal(() => AccessModel.Load(model.Roles, [new Assignment(P.Alice, AccessModel.BreakGlassCustodian)], model.IncompatiblePairs, 5)).ShouldBe(AccessRefusal.RoleNotAssignable);
        P.Refusal(() => AccessModel.Load([new Role("bad", "Bad", ["no.such.action"])], [], [], 5)).ShouldBe(AccessRefusal.UnknownAction);

        var loaded = AccessModel.Load(model.Roles, model.Assignments, model.IncompatiblePairs, 7);
        loaded.Version.ShouldBe(7);
        loaded.Can(P.Manager, ActionCatalog.ChangesApprove).ShouldBeTrue();
    }

    [Fact]
    public void AModelCanNotBeChangedThroughWhatItHandsOut()
    {
        var model = P.Started();
        var role = model.Roles.Single(r => r.Code == AccessModel.Auditor);
        ((ICollection<string>)role.Actions).IsReadOnly.ShouldBeTrue();
        var actions = new[] { ActionCatalog.RolesRead };
        var next = P.ProposeAndApprove(model, new DefineRole("helpdesk", "Helpdesk", actions));
        actions[0] = ActionCatalog.ChangesApprove;
        next.Roles.Single(r => r.Code == "helpdesk").Actions.ShouldBe([ActionCatalog.RolesRead]);
    }
}

public sealed class ChangeRequestTests
{
    private static readonly DateTimeOffset T0 = P.T0;

    [Fact]
    public void AnApprovedChangeRecordsWhoProposedAndWhoApproved()
    {
        var model = P.Started();
        var change = ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.Auditor), P.Admin, "Quarterly review", model, T0);
        change.State.ShouldBe(ChangeState.Proposed);
        change.ProposedAgainstVersion.ShouldBe(model.Version);

        var next = change.Approve(P.Manager, "Checked", model, T0.AddMinutes(5));
        change.State.ShouldBe(ChangeState.Approved);
        change.Maker.ShouldBe(P.Admin);
        change.Checker.ShouldBe(P.Manager);
        change.AppliedAsVersion.ShouldBe(next.Version);
        change.DecidedAtUtc.ShouldBe(T0.AddMinutes(5));
        next.RolesOf(P.Alice).ShouldBe([AccessModel.Auditor]);
    }

    [Fact]
    public void TheMakerCanNeverDecideTheirOwnChange()
    {
        // The maker proposes, then moves from IAM Administrator to IAM Manager (through other people), so they can now
        // approve. Only the maker rule can stop them deciding their own change.
        var model = P.ProposeAndApprove(P.Started(), new AssignRole(P.Bob, AccessModel.IamAdministrator));
        var change = ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.Auditor), P.Admin, "Needed", model, T0);
        model = P.ProposeAndApprove(model, new RemoveAssignment(P.Admin, AccessModel.IamAdministrator), P.Bob, P.Manager);
        model = P.ProposeAndApprove(model, new AssignRole(P.Admin, AccessModel.IamManager), P.Bob, P.Manager);
        model.Can(P.Admin, ActionCatalog.ChangesApprove).ShouldBeTrue();
        P.Refusal(() => change.Approve(P.Admin, null, model, T0)).ShouldBe(AccessRefusal.MakerCannotDecide);
        P.Refusal(() => change.Reject(P.Admin, "No", model, T0)).ShouldBe(AccessRefusal.MakerCannotDecide);
        change.State.ShouldBe(ChangeState.Proposed);
    }

    [Fact]
    public void OnlySomeoneAllowedCanProposeOrDecide()
    {
        var model = P.Started();
        P.Refusal(() => ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.Auditor), P.Bob, "Needed", model, T0)).ShouldBe(AccessRefusal.NotAllowed);
        P.Refusal(() => ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.Auditor), P.Manager, "Needed", model, T0)).ShouldBe(AccessRefusal.NotAllowed);

        var change = ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.Auditor), P.Admin, "Needed", model, T0);
        P.Refusal(() => change.Approve(P.Bob, null, model, T0)).ShouldBe(AccessRefusal.NotAllowed);
        P.Refusal(() => change.Reject(P.Bob, "No", model, T0)).ShouldBe(AccessRefusal.NotAllowed);
    }

    [Fact]
    public void NobodyCanProposeARoleForThemselves()
    {
        var model = P.Started();
        P.Refusal(() => ChangeRequest.Propose(P.NewId(), new AssignRole(P.Admin, AccessModel.PolicyAuthor), P.Admin, "Needed", model, T0)).ShouldBe(AccessRefusal.SelfGrant);
    }

    [Fact]
    public void NobodyCanApproveARoleForThemselves()
    {
        var model = P.Started();
        var change = ChangeRequest.Propose(P.NewId(), new AssignRole(P.Manager, AccessModel.PolicyAuthor), P.Admin, "Needed", model, T0);
        P.Refusal(() => change.Approve(P.Manager, null, model, T0)).ShouldBe(AccessRefusal.SelfGrant);
        change.State.ShouldBe(ChangeState.Proposed);
    }

    [Fact]
    public void NobodyCanChangeTheActionsOfARoleTheyHold()
    {
        var model = P.Started();
        P.Refusal(() => ChangeRequest.Propose(P.NewId(), new DefineRole(AccessModel.IamAdministrator, "IAM Administrator", [ActionCatalog.RolesRead, ActionCatalog.ChangesRead, ActionCatalog.ChangesPropose, ActionCatalog.ApprovalsEscalate]), P.Admin, "Needed", model, T0)).ShouldBe(AccessRefusal.SelfGrant);

        var change = ChangeRequest.Propose(P.NewId(), new DefineRole(AccessModel.IamManager, "IAM Manager", [ActionCatalog.RolesRead, ActionCatalog.ChangesRead, ActionCatalog.ChangesApprove]), P.Admin, "Needed", model, T0);
        P.Refusal(() => change.Approve(P.Manager, null, model, T0)).ShouldBe(AccessRefusal.SelfGrant);
    }

    [Fact]
    public void RulesAreCheckedAgainOnApprovalAgainstTheModelAsItIsThen()
    {
        // Proposed while legal; by approval time Alice already holds the incompatible role.
        var model = P.Started();
        var first = ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.PolicyAuthor), P.Admin, "Needed", model, T0);
        var second = ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.PolicyChecker), P.Admin, "Needed", model, T0);
        model = first.Approve(P.Manager, null, model, T0);
        P.Refusal(() => second.Approve(P.Manager, null, model, T0)).ShouldBe(AccessRefusal.IncompatibleRoles);
        second.State.ShouldBe(ChangeState.Proposed);
        model.RolesOf(P.Alice).ShouldBe([AccessModel.PolicyAuthor]);
    }

    [Fact]
    public void ACheckerWhoGainedTheRoleSinceTheProposalCanNotApproveIt()
    {
        var model = P.WithSecondManager();
        var change = ChangeRequest.Propose(P.NewId(), new DefineRole(AccessModel.PolicyAuthor, "Policy author", [ActionCatalog.RolesRead]), P.Admin, "Needed", model, T0);
        model = P.ProposeAndApprove(model, new AssignRole(P.Manager, AccessModel.PolicyAuthor), P.Admin, P.Second);
        P.Refusal(() => change.Approve(P.Manager, null, model, T0)).ShouldBe(AccessRefusal.SelfGrant);
    }

    [Fact]
    public void AMakerWhoGainedTheRoleSinceProposingCanNotHaveItApproved()
    {
        var model = P.ProposeAndApprove(P.Started(), new AssignRole(P.Bob, AccessModel.IamAdministrator));
        var change = ChangeRequest.Propose(P.NewId(), new DefineRole(AccessModel.PolicyAuthor, "Policy author", [ActionCatalog.RolesRead]), P.Admin, "Needed", model, T0);
        model = P.ProposeAndApprove(model, new AssignRole(P.Admin, AccessModel.PolicyAuthor), P.Bob, P.Manager);
        P.Refusal(() => change.Approve(P.Manager, null, model, T0)).ShouldBe(AccessRefusal.SelfGrant);
    }

    [Fact]
    public void ReasonsHaveALengthLimit()
    {
        var model = P.Started();
        var longest = new string('x', AccessLimits.MaxReasonLength);
        Should.NotThrow(() => ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.Auditor), P.Admin, longest, model, T0));
        P.Refusal(() => ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.Auditor), P.Admin, longest + "x", model, T0)).ShouldBe(AccessRefusal.InvalidReason);
    }

    [Fact]
    public void ADecidedChangeIsFinal()
    {
        var model = P.Started();
        var change = ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.Auditor), P.Admin, "Needed", model, T0);
        var next = change.Approve(P.Manager, null, model, T0);
        P.Refusal(() => change.Approve(P.Manager, null, next, T0)).ShouldBe(AccessRefusal.NotProposed);
        P.Refusal(() => change.Reject(P.Manager, "No", next, T0)).ShouldBe(AccessRefusal.NotProposed);
        P.Refusal(() => change.Withdraw(P.Admin, null, T0)).ShouldBe(AccessRefusal.NotProposed);
    }

    [Fact]
    public void OnlyTheMakerCanWithdrawAndARejectionNeedsAReason()
    {
        var model = P.Started();
        var change = ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.Auditor), P.Admin, "Needed", model, T0);
        P.Refusal(() => change.Withdraw(P.Manager, null, T0)).ShouldBe(AccessRefusal.NotMaker);
        P.Refusal(() => change.Reject(P.Manager, null, model, T0)).ShouldBe(AccessRefusal.InvalidReason);
        change.Withdraw(P.Admin, "Not needed", T0);
        change.State.ShouldBe(ChangeState.Withdrawn);

        var other = ChangeRequest.Propose(P.NewId(), new AssignRole(P.Bob, AccessModel.Auditor), P.Admin, "Needed", model, T0);
        other.Reject(P.Manager, "Not justified", model, T0);
        other.State.ShouldBe(ChangeState.Rejected);
        other.DecisionReason.ShouldBe("Not justified");
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("a{0}b", 0x202E)]
    [InlineData("a{0}b", 0x2028)]
    [InlineData("a{0}b", 0x0007)]
    public void AProposalNeedsASafeReason(string? template, int character)
    {
        var reason = template?.Replace("{0}", ((char)character).ToString(), StringComparison.Ordinal);
        P.Refusal(() => ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.Auditor), P.Admin, reason, P.Started(), P.T0)).ShouldBe(AccessRefusal.InvalidReason);
    }

    [Fact]
    public void AnInvalidChangeIsRefusedWhenProposed()
    {
        var model = P.Started();
        P.Refusal(() => ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.BreakGlassCustodian), P.Admin, "Needed", model, T0)).ShouldBe(AccessRefusal.RoleNotAssignable);
        P.Refusal(() => ChangeRequest.Propose(Guid.Empty, new AssignRole(P.Alice, AccessModel.Auditor), P.Admin, "Needed", model, T0)).ShouldBe(AccessRefusal.InvalidChange);
    }

    [Fact]
    public void RefusalMessagesShowNoPersonOrReason()
    {
        var model = P.Started();
        var refusal = Should.Throw<AccessRefusedException>(() => ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.Auditor), P.Bob, "Secret reason", model, T0));
        refusal.Message.ShouldNotContain(P.Bob);
        refusal.Message.ShouldNotContain(P.Alice);
        refusal.Message.ShouldNotContain("Secret reason");
    }
}

/// <summary>Whatever order changes arrive in, the rules hold, checked here independently of the model's own code.</summary>
public sealed class AccessInvariantTests
{
    private static readonly string[] People = [P.Admin, P.Manager, P.Second, P.Alice, P.Bob, "sub-carol"];
    private static readonly string[] RoleCodes = [AccessModel.IamAdministrator, AccessModel.IamManager, AccessModel.Auditor, AccessModel.PolicyAuthor, AccessModel.PolicyChecker, AccessModel.BreakGlassCustodian, "helpdesk"];

#pragma warning disable CA5394 // A fixed seed on purpose: the generated sequences must be the same on every run.
    [Fact]
    public void GeneratedSequencesNeverBreakTheRules()
    {
        var random = new Random(20261110);
        var approved = 0;
        var refused = 0;
        for (var round = 0; round < 1_000; round++)
        {
            var model = P.Started();
            var open = new List<ChangeRequest>();
            for (var step = 0; step < 30; step++)
            {
                var before = Describe(model);
                try
                {
                    if (open.Count > 0 && random.Next(2) == 0)
                    {
                        var change = open[random.Next(open.Count)];
                        var person = People[random.Next(People.Length)];
                        var maker = change.Maker;
                        var next = change.Approve(person, null, model, P.T0);
                        CheckApproval(change, maker, person, model, next);
                        model = next;
                        open.Remove(change);
                        approved++;
                    }
                    else
                    {
                        open.Add(ChangeRequest.Propose(P.NewId(), RandomChange(random), People[random.Next(People.Length)], "Generated", model, P.T0));
                    }
                }
                catch (AccessRefusedException)
                {
                    refused++;
                    Describe(model).ShouldBe(before);
                }

                CheckInvariants(model);
            }
        }

        approved.ShouldBeGreaterThan(500);
        refused.ShouldBeGreaterThan(1_000);
    }

    private static AccessChange RandomChange(Random random)
    {
        string Person() => People[random.Next(People.Length)];
        string Role() => RoleCodes[random.Next(RoleCodes.Length)];
        return random.Next(6) switch
        {
            0 or 1 => new AssignRole(Person(), Role()),
            2 => new RemoveAssignment(Person(), Role()),
            3 => new DefineRole(Role(), "Generated", [.. ActionCatalog.All.Where(a => !a.Everyone && random.Next(3) == 0).Select(a => a.Id)]),
            4 => new AddIncompatiblePair(Role(), Role()),
            _ => new RemoveIncompatiblePair(Role(), Role()),
        };
    }

    private static string Describe(AccessModel model) =>
        string.Join('|', model.Version, string.Join(',', model.Assignments), string.Join(',', model.Roles.Select(r => r.Code + ":" + string.Join('+', r.Actions))), string.Join(',', model.IncompatiblePairs));

    private static void CheckApproval(ChangeRequest change, string maker, string checker, AccessModel before, AccessModel after)
    {
        (maker != checker).ShouldBeTrue();
        before.Can(checker, ActionCatalog.ChangesApprove).ShouldBeTrue();
        (after.Version == before.Version + 1).ShouldBeTrue();

        // Nobody gained a role, or a widened role, through a change they made or decided.
        foreach (var person in new[] { maker, checker })
        {
            var gained = after.RolesOf(person).Except(before.RolesOf(person)).Any();
            gained.ShouldBeFalse();
            if (change.Change is DefineRole define)
            {
                before.RolesOf(person).Contains(define.Code).ShouldBeFalse();
            }
        }
    }

    private static void CheckInvariants(AccessModel model)
    {
        // Nobody holds two roles of an incompatible pair, worked out from the model's lists rather than its checks.
        foreach (var person in model.Assignments.Select(a => a.Subject).Distinct())
        {
            var held = model.RolesOf(person);
            model.IncompatiblePairs.Any(p => held.Contains(p.First) && held.Contains(p.Second)).ShouldBeFalse();
        }

        // Nobody holds a role that can't be assigned, and every action given is a real, grantable action.
        var assignable = model.Roles.Where(r => r.Assignable).Select(r => r.Code).ToHashSet();
        model.Assignments.All(a => assignable.Contains(a.RoleCode)).ShouldBeTrue();
        model.Roles.SelectMany(r => r.Actions).All(a => ActionCatalog.IsKnown(a) && !ActionCatalog.IsEveryone(a)).ShouldBeTrue();

        // Someone can still propose, and a different person approve.
        var people = model.Assignments.Select(a => a.Subject).Distinct().ToList();
        var proposers = people.Where(p => RoleActions(model, p).Contains(ActionCatalog.ChangesPropose)).ToList();
        var approvers = people.Where(p => RoleActions(model, p).Contains(ActionCatalog.ChangesApprove)).ToList();
        proposers.Any(p => approvers.Any(a => a != p)).ShouldBeTrue();

        // Nobody holds two conflicting duties, and no role gives both (DL-057d, DL-060a, DL-061b).
        foreach (var (first, second) in ActionCatalog.ConflictingActions)
        {
            people.Any(p => RoleActions(model, p).Contains(first) && RoleActions(model, p).Contains(second)).ShouldBeFalse();
            model.Roles.Any(r => r.Actions.Contains(first) && r.Actions.Contains(second)).ShouldBeFalse();
        }
    }

    private static HashSet<string> RoleActions(AccessModel model, string person) =>
        [.. model.Roles.Where(r => model.RolesOf(person).Contains(r.Code)).SelectMany(r => r.Actions)];
#pragma warning restore CA5394
}

/// <summary>Review fixes (stage 2 slice 2 review): separated duties on actions, revoked makers, the model hash.</summary>
public sealed class AccessReviewFixTests
{
    private static readonly DateTimeOffset T0 = P.T0;

    [Fact]
    public void NoRoleMayHoldBothProposingAndApproving()
    {
        var model = P.Started();
        P.Refusal(() => model.Check(new DefineRole("change-lead", "Lead", [ActionCatalog.ChangesPropose, ActionCatalog.ChangesApprove])))
            .ShouldBe(AccessRefusal.SeparatedDuties);
        P.Refusal(() => model.Check(new DefineRole(AccessModel.Auditor, "Auditor", [ActionCatalog.ChangesPropose, ActionCatalog.ChangesApprove])))
            .ShouldBe(AccessRefusal.SeparatedDuties);
        P.Refusal(() => AccessModel.Load([new Role("change-lead", "Lead", [ActionCatalog.ChangesPropose, ActionCatalog.ChangesApprove])], [], [], 1))
            .ShouldBe(AccessRefusal.SeparatedDuties);
    }

    [Fact]
    public void NoPersonMayHoldBothThroughTwoRoles()
    {
        // Two new roles with no incompatible pair between them: one proposes, one approves.
        var model = P.ProposeAndApprove(P.Started(), new DefineRole("drafter", "Drafter", [ActionCatalog.ChangesPropose]));
        model = P.ProposeAndApprove(model, new DefineRole("signer", "Signer", [ActionCatalog.ChangesApprove]));
        model = P.ProposeAndApprove(model, new AssignRole(P.Alice, "drafter"));
        P.Refusal(() => model.Check(new AssignRole(P.Alice, "signer"))).ShouldBe(AccessRefusal.SeparatedDuties);

        // Nor by giving an approver a proposing role, or a role they hold the other duty.
        model = P.ProposeAndApprove(model, new AssignRole(P.Bob, "signer"));
        P.Refusal(() => model.Check(new AssignRole(P.Bob, AccessModel.IamAdministrator))).ShouldBe(AccessRefusal.SeparatedDuties);
        P.Refusal(() => model.Check(new AssignRole(P.Bob, "drafter"))).ShouldBe(AccessRefusal.SeparatedDuties);
        P.Refusal(() => model.Check(new DefineRole("signer", "Signer", [ActionCatalog.ChangesApprove, ActionCatalog.ChangesPropose])))
            .ShouldBe(AccessRefusal.SeparatedDuties);
        Should.NotThrow(() => model.Check(new DefineRole(AccessModel.Auditor, "Auditor", [ActionCatalog.ChangesPropose])));

        // A stored model where one person holds both is refused on load.
        P.Refusal(() => AccessModel.Load(model.Roles, [.. model.Assignments, new Assignment(P.Alice, "signer")], model.IncompatiblePairs, model.Version))
            .ShouldBe(AccessRefusal.SeparatedDuties);
    }

    [Fact]
    public void ProposingAndEscalatingAreSeparateDutiesToo()
    {
        ActionCatalog.ConflictingActions.ShouldContain((ActionCatalog.ChangesPropose, ActionCatalog.ApprovalsEscalate));
        var model = P.Started();
        P.Refusal(() => model.Check(new DefineRole("escalating-admin", "Escalating admin", [ActionCatalog.ChangesPropose, ActionCatalog.ApprovalsEscalate])))
            .ShouldBe(AccessRefusal.SeparatedDuties);
        P.Refusal(() => model.Check(new DefineRole(AccessModel.IamAdministrator, "IAM Administrator", [ActionCatalog.ChangesPropose, ActionCatalog.ApprovalsEscalate])))
            .ShouldBe(AccessRefusal.SeparatedDuties);

        // Through two roles: an escalation-only role given to an administrator.
        model = P.ProposeAndApprove(model, new DefineRole("escalator", "Escalator", [ActionCatalog.ApprovalsEscalate]));
        P.Refusal(() => model.Check(new AssignRole(P.Admin, "escalator"))).ShouldBe(AccessRefusal.SeparatedDuties);
        Should.NotThrow(() => model.Check(new AssignRole(P.Alice, "escalator")));
    }

    [Fact]
    public void TheHashNamesItsScheme()
    {
        AccessModel.HashScheme.ShouldBe("v1");
        var model = P.Started();
        model.ContentHash().ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void AProposalWhoseMakerCanNoLongerProposeCannotBeApproved()
    {
        var model = P.ProposeAndApprove(P.Started(), new AssignRole(P.Bob, AccessModel.IamAdministrator));
        var change = ChangeRequest.Propose(P.NewId(), new AssignRole(P.Alice, AccessModel.Auditor), P.Bob, "Needed", model, T0);
        model = P.ProposeAndApprove(model, new RemoveAssignment(P.Bob, AccessModel.IamAdministrator));
        P.Refusal(() => change.Approve(P.Manager, null, model, T0)).ShouldBe(AccessRefusal.MakerNoLongerAllowed);
        change.State.ShouldBe(ChangeState.Proposed);

        // It can still be rejected, so it doesn't stay open for ever.
        change.Reject(P.Manager, "Maker left", model, T0);
        change.State.ShouldBe(ChangeState.Rejected);
    }

    [Fact]
    public void SubjectsAreBoundedLikeEveryStoredActor()
    {
        AccessLimits.MaxSubjectLength.ShouldBe(200);
        var model = P.Started();
        Should.NotThrow(() => model.Check(new AssignRole(new string('a', 200), AccessModel.Auditor)));
        P.Refusal(() => model.Check(new AssignRole(new string('a', 201), AccessModel.Auditor))).ShouldBe(AccessRefusal.InvalidSubject);
    }

    [Fact]
    public void TheHashCoversTheWholeModelButNotItsVersion()
    {
        var model = P.Started();
        var same = AccessModel.Load(model.Roles, model.Assignments.Reverse(), model.IncompatiblePairs.Reverse(), model.Version + 7);
        same.ContentHash().ShouldBe(model.ContentHash());
        model.ContentHash().Length.ShouldBe(64);

        var variants = new[]
        {
            AccessModel.Load(model.Roles, [.. model.Assignments, new Assignment(P.Alice, AccessModel.Auditor)], model.IncompatiblePairs, model.Version),
            AccessModel.Load(model.Roles.Select(r => r.Code == AccessModel.Auditor ? r with { Name = "Auditors" } : r), model.Assignments, model.IncompatiblePairs, model.Version),
            AccessModel.Load(model.Roles.Select(r => r.Code == AccessModel.Auditor ? r with { Actions = [ActionCatalog.RolesRead] } : r), model.Assignments, model.IncompatiblePairs, model.Version),
            AccessModel.Load(model.Roles.Select(r => r.Code == AccessModel.PolicyAuthor ? r with { Assignable = false } : r), model.Assignments, model.IncompatiblePairs, model.Version),
            AccessModel.Load(model.Roles, model.Assignments, model.IncompatiblePairs.Skip(1), model.Version),
            AccessModel.Load(model.Roles, model.Assignments, [.. model.IncompatiblePairs.Skip(1), IncompatiblePair.Of(AccessModel.Auditor, AccessModel.PolicyChecker)], model.Version),
            AccessModel.Load(model.Roles, model.Assignments, [.. model.IncompatiblePairs.Skip(1), IncompatiblePair.Of(AccessModel.IamManager, AccessModel.PolicyChecker)], model.Version),
        };
        variants.Select(v => v.ContentHash()).Append(model.ContentHash()).Distinct().Count().ShouldBe(variants.Length + 1);

        // Field boundaries are unambiguous: moving characters between two fields changes the hash.
        var a = AccessModel.Load([new Role("ab", "c", [])], [], [], 1).ContentHash();
        var b = AccessModel.Load([new Role("a", "bc", [])], [], [], 1).ContentHash();
        a.ShouldNotBe(b);

        // Both roles of a pair count: two one-pair models that differ only in the pair's first role.
        var roles = P.Started().Roles;
        AccessModel.Load(roles, [], [IncompatiblePair.Of(AccessModel.Auditor, AccessModel.PolicyChecker)], 1).ContentHash()
            .ShouldNotBe(AccessModel.Load(roles, [], [IncompatiblePair.Of(AccessModel.IamManager, AccessModel.PolicyChecker)], 1).ContentHash());
    }
}
